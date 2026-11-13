#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PnP.Scanning.Core.ClassicPages.Collection.Module;

/// <summary>
/// Keeps the opened connection of a Web between consecutive reads of that Web, so each read reuses it instead of
/// opening its own. A connection is handed to one reader at a time: taking it removes it from the pool and returning
/// it puts it back. The pool holds a bounded number of waiting connections and closes the one that has waited longest
/// when a newer one arrives. An answer from a kept connection is returned only when the caller accepts it; any other
/// answer is read again on a newly opened connection, and it stands only when that second read fails.
/// </summary>
internal sealed class WebSessionPool<TSession>(int capacity) : IDisposable
    where TSession : class, IDisposable
{
    private readonly ConcurrentDictionary<string, (TSession Session, long Order)> waiting =
        new(StringComparer.Ordinal);

    private readonly int limit = Math.Max(1, capacity);
    private long order;
    private int closed;

    /// <summary>
    /// Counts the connections that wait for their next read.
    /// </summary>
    internal int Count => waiting.Count;

    /// <summary>
    /// Runs one read of a Web. The read uses the Web's waiting connection when that connection is usable, and its
    /// answer is returned when the caller accepts it. In every other case the waiting connection is closed and the
    /// read runs on a newly opened connection, whose answer is the outcome. When the read on the new connection
    /// fails, the answer the waiting connection gave stands; with no such answer the failure is the outcome. A
    /// connection is kept for the next read only after an answer the caller marks reusable.
    /// </summary>
    internal async Task<TResult> ReadAsync<TResult>(
        string key,
        Func<TSession, bool> usable,
        Func<Task<TSession>> open,
        Func<TSession, Task<TResult>> read,
        Func<TResult, bool> accept,
        Func<TResult, bool> reusable,
        CancellationToken token)
    {
        bool answered = false;
        TResult? keptAnswer = default;
        if (TryTake(key, out TSession? kept))
        {
            if (!usable(kept))
                kept.Dispose();
            else
            {
                try
                {
                    TResult result = await SettleAsync(key, kept, read, reusable).ConfigureAwait(false);
                    if (accept(result)) return result;
                    keptAnswer = result;
                    answered = true;
                }
                catch (Exception error) when (error is not OperationCanceledException &&
                                              !token.IsCancellationRequested)
                {
                    Serilog.Log.Debug(
                        error,
                        "A read on the kept connection of {Web} failed; reading again on a new connection",
                        key);
                }
            }
        }

        token.ThrowIfCancellationRequested();
        try
        {
            TSession opened = await open().ConfigureAwait(false);
            return await SettleAsync(key, opened, read, reusable).ConfigureAwait(false);
        }
        catch (Exception error) when (answered && error is not OperationCanceledException &&
                                      !token.IsCancellationRequested)
        {
            Serilog.Log.Debug(
                error,
                "The repeated read on a new connection of {Web} failed; the kept connection's answer stands",
                key);
            return keptAnswer!;
        }
    }

    /// <summary>
    /// Runs a read on a connection the caller owns, then keeps the connection after a reusable answer and closes it
    /// after any other answer or a failure.
    /// </summary>
    private async Task<TResult> SettleAsync<TResult>(
        string key,
        TSession session,
        Func<TSession, Task<TResult>> read,
        Func<TResult, bool> reusable)
    {
        bool keep = false;
        try
        {
            TResult result = await read(session).ConfigureAwait(false);
            keep = reusable(result);
            return result;
        }
        finally
        {
            if (keep)
                Return(key, session);
            else
                session.Dispose();
        }
    }

    /// <summary>
    /// Hands the waiting connection of a Web to the caller, who then owns it until it is returned or disposed.
    /// </summary>
    internal bool TryTake(string key, [NotNullWhen(true)] out TSession? session)
    {
        if (waiting.TryRemove(key, out (TSession Session, long Order) entry))
        {
            session = entry.Session;
            return true;
        }

        session = null;
        return false;
    }

    /// <summary>
    /// Keeps a connection for the next read of its Web. A connection that cannot be kept is closed: one returned
    /// after the pool closed, one whose Web already has a waiting connection, and the longest-waiting connections
    /// beyond the capacity.
    /// </summary>
    internal void Return(string key, TSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Volatile.Read(ref closed) != 0 || !waiting.TryAdd(key, (session, Interlocked.Increment(ref order))))
        {
            session.Dispose();
            return;
        }

        if (Volatile.Read(ref closed) != 0)
        {
            Close(key);
            return;
        }

        while (waiting.Count > limit)
        {
            string? oldest = null;
            long oldestOrder = long.MaxValue;
            foreach (KeyValuePair<string, (TSession Session, long Order)> entry in waiting)
            {
                if (entry.Value.Order >= oldestOrder) continue;
                oldest = entry.Key;
                oldestOrder = entry.Value.Order;
            }

            if (oldest == null) break;
            Close(oldest);
        }
    }

    /// <summary>
    /// Closes every waiting connection and every connection returned afterwards.
    /// </summary>
    public void Dispose()
    {
        Volatile.Write(ref closed, 1);
        foreach (string key in waiting.Keys) Close(key);
    }

    /// <summary>
    /// Closes the waiting connection of one Web when it is still waiting.
    /// </summary>
    private void Close(string key)
    {
        if (waiting.TryRemove(key, out (TSession Session, long Order) entry)) entry.Session.Dispose();
    }
}
