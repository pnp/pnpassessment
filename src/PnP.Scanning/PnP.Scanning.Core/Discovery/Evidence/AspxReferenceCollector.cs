namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Per-Web acquisition evidence buffered while SharePoint requests run. The authentic Classic
/// assessment drains every category into assessment.db before the Web worker completes.
/// </summary>
internal sealed class AspxReferenceCollector
{
    private readonly object gate = new();
    private readonly List<AspxReferenceCandidate> candidates = new();
    private readonly List<AspxSurfaceDenominatorRow> denominator = new();
    private readonly List<AspxPaginationPageReceipt> pagination = new();
    private readonly HashSet<string> gaps = new(StringComparer.Ordinal);

    internal IReadOnlyList<AspxSurfaceDenominatorRow> ReadSurfaceEvidence()
    {
        lock (gate) return denominator.ToArray();
    }

    internal IReadOnlyList<AspxReferenceCandidate> ReadReferenceEvidence()
    {
        lock (gate) return candidates.ToArray();
    }

    internal IReadOnlyList<AspxPaginationPageReceipt> ReadPaginationEvidence()
    {
        lock (gate) return pagination.ToArray();
    }

    internal IReadOnlyList<string> ReadGaps()
    {
        lock (gate) return gaps.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    internal void AddReference(AspxReferenceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (gate) candidates.Add(candidate);
    }

    internal void AddSurface(AspxSurfaceDenominatorRow row,
        IReadOnlyList<AspxPaginationPageReceipt> pageReceipts,
        IReadOnlyList<string> gapCodes = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (gate)
        {
            denominator.Add(row);
            pagination.AddRange(pageReceipts ?? Array.Empty<AspxPaginationPageReceipt>());
            foreach (var gap in gapCodes ?? Array.Empty<string>()) gaps.Add(gap);
        }
    }

    internal void AddGap(string gap)
    {
        if (string.IsNullOrWhiteSpace(gap)) return;
        lock (gate) gaps.Add(gap);
    }
}
