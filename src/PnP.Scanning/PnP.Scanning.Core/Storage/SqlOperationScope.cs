#nullable enable
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PnP.Scanning.Core.Storage;

/// <summary>Shares local timing, static call-site capture and asynchronous correlation across database access paths.</summary>
internal sealed class SqlOperationScope : IDisposable
{
    private static readonly AsyncLocal<SqlOperationScope?> CurrentScope = new();
    private readonly SqlOperationScope? parent;
    private readonly Action<SqlLogRecord> write;
    private readonly bool installed;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly string member;
    private readonly string file;
    private readonly int line;
    private readonly string kind;
    private bool disposed;
    private string outcome = "finished";
    private string? errorType;

    /// <summary>Captures a static code location and optionally installs this operation as the asynchronous parent.</summary>
    internal SqlOperationScope(
        string operation,
        string? phase,
        string file,
        string member,
        int line,
        string kind = "operation",
        bool install = false,
        Action<SqlLogRecord>? write = null)
    {
        parent = CurrentScope.Value;
        Operation = operation;
        Phase = phase ?? parent?.Phase;
        OperationId = Guid.NewGuid();
        this.member = member;
        string normalized = file.Replace('\\', '/');
        int source = normalized.LastIndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        this.file = source >= 0 ? normalized[(source + 1)..] : Path.GetFileName(normalized);
        this.line = line;
        this.kind = kind;
        this.write = write ?? parent?.write ?? SqlLogRecord.Write;
        installed = install;
        if (install) CurrentScope.Value = this;
    }

    internal static SqlOperationScope? Current => CurrentScope.Value;
    internal string Operation { get; }
    internal string? Phase { get; }
    internal Guid OperationId { get; }

    /// <summary>Times a scope with an observed finish, without inferring success from disposal.</summary>
    internal static IDisposable Begin(
        string operation,
        string? phase = null,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) =>
        new SqlOperationScope(operation, phase, file, member, line, install: true);

    /// <summary>Times a database batch and preserves its exception and cancellation behavior.</summary>
    internal static async Task RunAsync(
        Func<Task> action,
        string? operation = null,
        string? phase = null,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
    {
        await RunAsync(
                async () =>
                {
                    await action().ConfigureAwait(false);
                    return true;
                },
                operation,
                phase,
                file,
                member,
                line)
            .ConfigureAwait(false);
    }

    /// <summary>Times a complete asynchronous operation, including result consumption inside the supplied action.</summary>
    internal static async Task<T> RunAsync<T>(
        Func<Task<T>> action,
        string? operation = null,
        string? phase = null,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
    {
        using SqlOperationScope scope = new(operation ?? member, phase, file, member, line, install: true);
        try
        {
            T result = await action().ConfigureAwait(false);
            scope.outcome = "success";
            return result;
        }
        catch (Exception exception)
        {
            scope.outcome = exception is OperationCanceledException ? "canceled" : "error";
            scope.errorType = exception.GetType().Name;
            throw;
        }
    }

    /// <summary>Writes an EF operation through the same local record format as timed database batches.</summary>
    internal void Record(string operationKind, string result, double elapsedMs, string? exceptionType = null) =>
        write(
            new SqlLogRecord(
                DateTimeOffset.UtcNow,
                Operation,
                OperationId,
                parent?.OperationId,
                Phase,
                operationKind,
                member,
                file,
                line,
                elapsedMs,
                result,
                exceptionType));

    /// <summary>Emits one duration and restores the parent when this scope installed it.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (installed) CurrentScope.Value = parent;
        Record(kind, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds, errorType);
    }
}