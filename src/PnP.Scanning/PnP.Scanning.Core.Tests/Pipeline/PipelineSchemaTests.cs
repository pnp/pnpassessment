#nullable enable
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

public sealed class PipelineSchemaTests
{
    [Fact]
    public void GeneratedSnapshotMatchesRuntimeModel()
    {
        using var db = new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite("Data Source=:memory:").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var types = new[]
        {
            typeof(SourceSnapshotRow), typeof(SourceObservationRow), typeof(PhaseRunRow),
            typeof(AnalysisRunRow), typeof(AnalysisResultRow), typeof(AnalysisReportRowEntry),
        };
        foreach (var type in types)
        {
            var entity = db.Model.FindEntityType(type)!;
            Assert.NotNull(entity.FindPrimaryKey());
            Assert.All(entity.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
        }
        var file = db.Model.FindEntityType(typeof(SPFile))!;
        Assert.True(file.FindProperty(nameof(SPFile.AspxEvidenceJson))!.IsNullable);
        Assert.Equal(new[] { "SiteCollectionId", "FileUniqueId" }, file.FindPrimaryKey()!.Properties.Select(x => x.Name));
    }

    [Fact]
    public async Task IncrementalUpgradeDowngradeAndReapplyPreservePublicRowsAndUnknownEvidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToArray();
        var prior = "20261030031028_SPFileFacts";
        Assert.Equal(prior, migrations[^2]);
        Assert.EndsWith("_PipelineFoundations", migrations[^1]);
        await migrator.MigrateAsync(prior);
        var assessment = Guid.NewGuid();
        var site = Guid.NewGuid();
        var file = Guid.NewGuid();
        db.Scans.Add(new Scan { ScanId = assessment, Status = ScanStatus.Paused, CLIMode = "Classic", Version = "fixture" });
        db.Properties.Add(new Property { ScanId = assessment, Name = "synthetic-option", Value = "preserved" });
        db.ClassicPages.Add(new ClassicPage
        {
            ScanId = assessment, SiteUrl = "https://example.invalid/sites/fixture", WebUrl = "/",
            PageUrl = "/preserved.aspx", CustomizedPageStatus = "Uncustomized",
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO SPFile
                (SiteCollectionId, FileUniqueId, ScanId, FileReadState, CoverageState, ProvenanceJson,
                 FirstObservedAtUtc, ObservedAtUtc, ItemMetadataReadState, ItemMetadataFieldStatesJson,
                 NoExecuteReadState, NoExecuteRaw, NoExecuteSource)
            VALUES ({{site}}, {{file}}, {{assessment}}, 'Unavailable', 'Unknown', '[]',
                    '2026-01-01', '2026-01-01', 'NotAttempted', '{}', 'NotAttempted', NULL, NULL)
            """);
        db.ChangeTracker.Clear();
        await migrator.MigrateAsync();
        var saved = await db.SPFiles.SingleAsync();
        Assert.Equal(site, saved.SiteCollectionId);
        Assert.Equal(file, saved.FileUniqueId);
        Assert.Equal(assessment, saved.ScanId);
        Assert.Null(saved.NoExecuteRaw);
        Assert.Null(saved.NoExecuteSource);
        Assert.Null(saved.WikiFieldHasValue);
        Assert.Null(saved.AspxEvidenceJson);
        Assert.Equal("NotAttempted", saved.NoExecuteReadState);
        Assert.Equal("Unknown", saved.CoverageState);
        Assert.Equal("Uncustomized", (await db.ClassicPages.SingleAsync()).CustomizedPageStatus);
        Assert.Equal("preserved", (await db.Properties.SingleAsync()).Value);
        Assert.Equal(ScanStatus.Paused, (await db.Scans.SingleAsync()).Status);
        Assert.Empty(await db.SourceSnapshots.ToListAsync());
        Assert.Empty(await db.PhaseRuns.ToListAsync());
        Assert.Empty(await db.AnalysisReportRows.ToListAsync());
        saved.AspxEvidenceJson = VersionedJson.From(new { state = "Unknown" }).Json;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await migrator.MigrateAsync(prior);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM SPFile WHERE NoExecuteRaw IS NULL AND NoExecuteReadState = 'NotAttempted'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        Assert.Equal("preserved", (await db.Properties.SingleAsync()).Value);
        Assert.Single(await db.ClassicPages.ToListAsync());
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Null((await db.SPFiles.SingleAsync()).AspxEvidenceJson);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task DatabaseEnforcesSnapshotOwnershipAndRestrictsDeletes()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observation = PipelineStoreFixture.Observation();
        await data.CommitAsync(observation);
        await data.Writer.SealAsync(default);
        var id = await data.StartAnalysisAsync();
        using (var db = data.Store.CreateContext(data.AssessmentId))
        {
            db.AnalysisResults.Add(new()
            {
                AnalysisRunId = id, ObservationId = observation.ObservationId, SnapshotId = Guid.NewGuid(),
                Outcome = AnalysisOutcome.Analyzed, Reason = "fixture", PayloadJson = VersionedJson.Empty.Json,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var db = data.Store.CreateContext(data.AssessmentId))
        {
            db.SourceSnapshots.Remove(await db.SourceSnapshots.SingleAsync());
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using var read = data.Store.CreateContext(data.AssessmentId, readOnly: true);
        Assert.Single(await read.SourceSnapshots.ToListAsync());
        Assert.Single(await read.SourceObservations.ToListAsync());
        Assert.Empty(await read.AnalysisResults.ToListAsync());
    }
}
