#nullable enable
using Serilog;

namespace PnP.Scanning.Core.Storage;

/// <summary>A local database timing and static code location, without SQL text, data values or exception messages.</summary>
internal sealed record SqlLogRecord(
    DateTimeOffset TimestampUtc,
    string Operation,
    Guid OperationId,
    Guid? ParentOperationId,
    string? Phase,
    string Kind,
    string Member,
    string File,
    int Line,
    double ElapsedMs,
    string Outcome,
    string? ErrorType = null)
{
    /// <summary>Writes only to the SQL-marked Serilog stream; no telemetry API is involved.</summary>
    internal static void Write(SqlLogRecord record) =>
        Log.ForContext("SqlLifecycle", true).Information("SQL timing {@Sql}", record);
}