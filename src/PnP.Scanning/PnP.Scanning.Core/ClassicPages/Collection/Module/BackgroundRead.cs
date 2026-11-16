#nullable enable
namespace PnP.Scanning.Core.ClassicPages.Collection.Module;

/// <summary>
/// Runs one read beside the rest of a collection. The read is cancelled with the collection and has ended by the
/// time this object is disposed, so it writes nothing after the collection has ended.
/// </summary>
internal sealed class BackgroundRead : IAsyncDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly Task read;

    private BackgroundRead(CancellationTokenSource stop, Task read)
    {
        this.stop = stop;
        this.read = read;
    }

    /// <summary>States whether the read has ended: finished, failed or cancelled.</summary>
    internal bool IsCompleted => read.IsCompleted;

    /// <summary>
    /// Starts <paramref name="read"/> beside the caller. The read is cancelled when <paramref name="token"/> is
    /// cancelled and when this object is disposed.
    /// </summary>
    internal static BackgroundRead Start(Func<CancellationToken, Task> read, CancellationToken token)
    {
        CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        return new(stop, Task.Run(() => read(stop.Token), CancellationToken.None));
    }

    /// <summary>Waits for the read to end and reports its failure or cancellation to the caller.</summary>
    internal Task WaitAsync() => read;

    /// <summary>
    /// Cancels a read that is still running and waits for it to end. The outcome of the read is reported by
    /// <see cref="WaitAsync"/>; here it gives way to whatever ended the collection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            await read.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reported by WaitAsync, or superseded by the failure that ended the collection.
        }

        stop.Dispose();
    }
}
