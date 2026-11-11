#nullable enable
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PnP.Scanning.Core.Storage;

/// <summary>Routes lightweight EF timings and context lifetime through the shared local database-operation recorder.</summary>
internal sealed class SqlLifecycleLogger : IDisposable
{
    private readonly SqlOperationScope context;
    private readonly Dictionary<Guid, long> transactions = new();

    /// <summary>Captures the context creation call site without installing a new asynchronous scope.</summary>
    internal SqlLifecycleLogger(string operation, string file, string member, int line, Action<SqlLogRecord>? write = null) =>
        context = new SqlOperationScope(operation, null, file, member, line, "context", write: write);

    /// <summary>Selects only timing payloads; EF-formatted SQL and parameter messages are never generated here.</summary>
    internal static bool Includes(EventId id, LogLevel level) =>
        id == RelationalEventId.CommandExecuted || id == RelationalEventId.CommandError ||
        id == RelationalEventId.CommandCanceled || id == RelationalEventId.TransactionStarted ||
        id == RelationalEventId.TransactionUsed || id == RelationalEventId.TransactionCommitted ||
        id == RelationalEventId.TransactionRolledBack || id == RelationalEventId.TransactionDisposed ||
        id == RelationalEventId.TransactionError;

    /// <summary>Records command execution and transaction durations without wrapping readers or changing database behavior.</summary>
    internal void Observe(EventData data)
    {
        SqlOperationScope scope = SqlOperationScope.Current ?? context;
        switch (data)
        {
            case CommandErrorEventData error:
                scope.Record("command", error.Exception is OperationCanceledException ? "canceled" : "error",
                    error.Duration.TotalMilliseconds, error.Exception.GetType().Name);
                break;
            case CommandExecutedEventData command:
                scope.Record("command", command.ExecuteMethod == DbCommandMethod.ExecuteReader ? "readerReturned" : "success",
                    command.Duration.TotalMilliseconds);
                break;
            case CommandEndEventData canceled when data.EventId == RelationalEventId.CommandCanceled:
                scope.Record("command", "canceled", canceled.Duration.TotalMilliseconds);
                break;
            case TransactionErrorEventData error:
                scope.Record("transactionOperation", error.Exception is OperationCanceledException ? "canceled" : "error",
                    error.Duration.TotalMilliseconds, error.Exception.GetType().Name);
                break;
            case TransactionEventData transaction:
                if (data.EventId == RelationalEventId.TransactionStarted || data.EventId == RelationalEventId.TransactionUsed)
                    transactions[transaction.TransactionId] = Stopwatch.GetTimestamp();
                else if (data.EventId == RelationalEventId.TransactionDisposed)
                {
                    if (transactions.Remove(transaction.TransactionId, out long started))
                        scope.Record("transaction", "disposed", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                else if (data is TransactionEndEventData end)
                {
                    string outcome = data.EventId == RelationalEventId.TransactionCommitted ? "committed" : "rolledBack";
                    scope.Record("transactionOperation", outcome, end.Duration.TotalMilliseconds);
                    if (transactions.Remove(transaction.TransactionId, out long started))
                        scope.Record("transaction", outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                break;
        }
    }

    /// <summary>Includes query consumption and bulk work in the observed context lifetime, without claiming business success.</summary>
    public void Dispose() => context.Dispose();
}
