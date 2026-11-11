#nullable enable
using System.Diagnostics;
using System.Text.Json;
using EFCore.BulkExtensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using PnP.Scanning.Core.Storage;
using Serilog;
using Xunit;
using Xunit.Abstractions;

namespace PnP.Scanning.Core.Tests.Storage;

/// <summary>Checks database timing coverage, static locations, privacy and unchanged exception behavior.</summary>
public sealed class SqlTimingTests(ITestOutputHelper output)
{
    /// <summary>Checks that one wrapper records completion, exception or cancellation without replacing the original exception.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("canceled")]
    public async Task WrapperRecordsOutcomeAndCodeLocation(string expected)
    {
        List<SqlLogRecord> records = new();
        using SqlOperationScope root = Recorder(records);
        Exception? failure = expected == "error"
            ? new InvalidOperationException("private-message")
            : expected == "canceled"
                ? new OperationCanceledException("private-message")
                : null;
        try
        {
            int result = await SqlOperationScope.RunAsync(
                async () =>
                {
                    await Task.Yield();
                    if (failure != null) throw failure;
                    return 42;
                },
                "SyntheticWrite");
            Assert.Equal(42, result);
        }
        catch (Exception exception)
        {
            Assert.Same(failure, exception);
        }

        SqlLogRecord record = Assert.Single(records);
        Assert.Equal(expected, record.Outcome);
        Assert.Equal("SyntheticWrite", record.Operation);
        Assert.Equal(nameof(WrapperRecordsOutcomeAndCodeLocation), record.Member);
        Assert.EndsWith("Storage/SqlTimingTests.cs", record.File);
        Assert.True(record.Line > 0);
        Assert.True(record.ElapsedMs > 0);
        Assert.Equal(root.OperationId, record.ParentOperationId);
        Assert.DoesNotContain("private-message", JsonSerializer.Serialize(record));
    }

    /// <summary>Checks synchronous/asynchronous EF commands, transactions and context lifetimes use the shared recorder.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EfAccessPathsRecordTimingAndStaticLocation(bool asynchronous)
    {
        List<SqlLogRecord> records = new();
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        using (ScanContext db = new(
                   new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options,
                   new SqlLifecycleLogger(
                       "SyntheticAccess",
                       @"C:\build\src\Storage\Probe.cs",
                       "Probe",
                       42,
                       records.Add)))
        {
            using IDbContextTransaction transaction = asynchronous
                ? await db.Database.BeginTransactionAsync()
                : db.Database.BeginTransaction();
            if (asynchronous)
            {
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE Probe (Id INTEGER)");
                Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT 7 AS Value").SingleAsync());
                await transaction.CommitAsync();
            }
            else
            {
                db.Database.ExecuteSqlRaw("CREATE TABLE Probe (Id INTEGER)");
                Assert.Equal(7, db.Database.SqlQueryRaw<int>("SELECT 7 AS Value").Single());
                transaction.Rollback();
            }
        }

        Assert.Contains(records, record => record.Kind == "command" && record.Outcome == "readerReturned");
        Assert.Contains(
            records,
            record => record.Kind == "transaction" && record.Outcome == (asynchronous ? "committed" : "rolledBack"));
        Assert.Contains(records, record => record.Kind == "transactionOperation");
        SqlLogRecord lifetime = Assert.Single(records, record => record.Kind == "context");
        Assert.Equal("finished", lifetime.Outcome);
        Assert.All(
            records,
            record =>
            {
                Assert.Equal("src/Storage/Probe.cs", record.File);
                Assert.Equal("Probe", record.Member);
                Assert.Equal(42, record.Line);
                Assert.True(record.ElapsedMs >= 0);
            });
    }

    /// <summary>Checks that direct-provider bulk work is timed as a whole batch without altering its algorithm.</summary>
    [Fact]
    public async Task BulkBatchAndEfFailuresAreCoveredWithoutDataValues()
    {
        List<SqlLogRecord> records = new();
        using SqlOperationScope root = Recorder(records);
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        using ScanContext db = new(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        records.Clear();
        await SqlOperationScope.RunAsync(
            () => db.BulkInsertAsync(
                new List<Property>
                {
                    new() { ScanId = Guid.NewGuid(), Name = "Synthetic", Type = "String", Value = "private-value" }
                }),
            "BulkInsert");
        SqlLogRecord batch = Assert.Single(records);
        Assert.Equal("BulkInsert", batch.Operation);
        Assert.Equal("success", batch.Outcome);
        Assert.Equal(nameof(BulkBatchAndEfFailuresAreCoveredWithoutDataValues), batch.Member);
        Assert.Equal(1, await db.Properties.CountAsync());
        await Assert.ThrowsAsync<SqliteException>(() =>
            db.Database.ExecuteSqlRawAsync("SELECT 'private-value' FROM MissingTable"));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            db.Database.ExecuteSqlRawAsync("SELECT 1", canceled.Token));
        Assert.Contains(records, record => record.Kind == "command" && record.Outcome == "error");
        Assert.Contains(records, record => record.Kind == "command" && record.Outcome == "canceled");
        string json = JsonSerializer.Serialize(records);
        Assert.DoesNotContain("private-value", json);
        Assert.DoesNotContain("MissingTable", json);
    }

    /// <summary>Checks nested asynchronous correlation is isolated and restores the parent.</summary>
    [Fact]
    public async Task ScopesRestoreAcrossParallelOperations()
    {
        List<SqlLogRecord> records = new();
        using SqlOperationScope root = Recorder(records);
        await Task.WhenAll(
            Enumerable.Range(0, 3)
                .Select(async index =>
                {
                    using IDisposable child = SqlOperationScope.Begin("Child");
                    Guid childId = SqlOperationScope.Current!.OperationId;
                    await Task.Yield();
                    Assert.Equal(childId, SqlOperationScope.Current.OperationId);
                }));
        Assert.Equal(root.OperationId, SqlOperationScope.Current!.OperationId);
        Assert.Equal(3, records.Count);
        Assert.All(records, record => Assert.Equal(root.OperationId, record.ParentOperationId));
        Assert.Equal(3, records.Select(record => record.OperationId).Distinct().Count());
    }

    /// <summary>Checks caller-supplied EF diagnostics are preserved rather than replaced by the timing observer.</summary>
    [Fact]
    public async Task ExistingEfDiagnosticCallbackIsPreserved()
    {
        List<SqlLogRecord> records = new();
        using SqlOperationScope root = Recorder(records);
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        List<string> existing = new();
        using ScanContext db = new(
            new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection)
                .LogTo(existing.Add, new[] { RelationalEventId.CommandExecuting })
                .Options);
        Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT 7 AS Value").SingleAsync());
        Assert.Contains("SELECT", Assert.Single(existing));
        Assert.Contains(records, record => record.Kind == "command");
    }

    /// <summary>Checks the product file routing and reports representative enabled serialization/file-sink cost.</summary>
    [Fact]
    public async Task BufferedFileRoutingAndEnabledLoggingCost()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sql-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const int count = 1000;
            long unlogged;
            long logged;
            using (Serilog.Core.Logger logger = PnP.Scanning.Process.Program.CreateLocalLogger(folder, "synthetic"))
            {
                Action<SqlLogRecord> write = record => logger.ForContext("SqlLifecycle", true)
                    .ForContext("ScanId", "synthetic")
                    .Information("SQL timing {@Sql}", record);
                for (int warmup = 0; warmup < 10; warmup++)
                    await SqlOperationScope.RunAsync(() => Task.CompletedTask, "Warmup");
                Stopwatch timer = Stopwatch.StartNew();
                for (int index = 0; index < count; index++) await Task.CompletedTask;
                unlogged = timer.ElapsedTicks;
                timer.Restart();
                using (SqlOperationScope root = new(
                           "Synthetic",
                           null,
                           @"C:\build\src\Probe.cs",
                           "Probe",
                           42,
                           install: true,
                           write: write))
                    for (int index = 0; index < count; index++)
                        await SqlOperationScope.RunAsync(() => Task.CompletedTask, "Batch");
                logged = timer.ElapsedTicks;
                logger.Information("Ordinary synthetic event");
            }

            string[] lines = File.ReadAllLines(Path.Combine(folder, "sql_synthetic.jsonl"));
            Assert.Equal(count + 1, lines.Length);
            using JsonDocument json = JsonDocument.Parse(lines[0]);
            JsonElement properties = json.RootElement.GetProperty("Properties");
            Assert.True(properties.GetProperty("SqlLifecycle").GetBoolean());
            Assert.Equal("Batch", properties.GetProperty("Sql").GetProperty("Operation").GetString());
            Assert.DoesNotContain("SQL timing", File.ReadAllText(Path.Combine(folder, "log_synthetic.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "synthetic", "log_synthetic.txt")));
            output.WriteLine(
                "Synthetic no-op loop {0} iterations: unlogged={1:F3}ms; enabled buffered logger={2:F3}ms; per-record delta={3:F3}us. Includes scope, JSON serialization and buffered file writes, not a production workload.",
                count,
                unlogged * 1000d / Stopwatch.Frequency,
                logged * 1000d / Stopwatch.Frequency,
                (logged - unlogged) * 1_000_000d / Stopwatch.Frequency / count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Installs a synthetic recorder without replacing the process-wide logger.</summary>
    private static SqlOperationScope Recorder(List<SqlLogRecord> records) =>
        new(
            "SyntheticRoot",
            "synthetic",
            @"C:\build\src\Probe.cs",
            "Probe",
            1,
            install: true,
            write: record =>
            {
                lock (records) records.Add(record);
            });
}