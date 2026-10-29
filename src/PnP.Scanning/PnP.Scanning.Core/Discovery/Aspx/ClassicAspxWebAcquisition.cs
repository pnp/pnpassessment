using PnP.Scanning.Core.Storage;
using System.Text.Json;
using static PnP.Scanning.Core.Discovery.SharePointAspxRestJson;
using static PnP.Scanning.Core.Discovery.SharePointAspxUrlPolicy;

namespace PnP.Scanning.Core.Discovery.Aspx;

/// <summary>Projects already acquired physical folder-file facts into inventory; it performs no SharePoint requests.</summary>
internal sealed class ClassicAspxWebAcquisition
{
    private const string PhysicalPageRowType = "Page";
    private const string InventoryGapScope = "AspxInventory";
    private const string UnprovedPhysicalIdentity = "physical_identity_or_scope_unproved";
    private readonly ClassicAspxWebAcquisitionContext context;
    private readonly SharePointAspxUrlPolicy urlPolicy;

    /// <summary>Validates the real observed scope and publication bound without creating or borrowing a network client.</summary>
    internal ClassicAspxWebAcquisition(ClassicAspxWebAcquisitionContext context)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        if (context.ScanId == Guid.Empty || context.SiteCollectionId == Guid.Empty || context.WebId == Guid.Empty ||
            !HasSameHttpsAuthority(context.SiteUrl, context.WebUrl) ||
            !IsPathWithinRoot(context.WebUrl.AbsolutePath, context.SiteUrl.AbsolutePath) || context.BatchSize is < 1 or > 200)
        {
            throw new ArgumentException("Physical ASPX inventory requires an exact observed scope and finite batch size.", nameof(context));
        }
        urlPolicy = new SharePointAspxUrlPolicy(context.SiteUrl, context.WebUrl);
    }

    /// <summary>
    /// Streams physical observations into bounded inventory batches. Metadata declarations cannot create resources,
    /// and publication failures or caller cancellation propagate instead of becoming successful empty observations.
    /// </summary>
    internal async Task AcquireAsync(IAsyncEnumerable<ClassicPageDiscovery> physicalPages,
        Func<ClassicAspxAcquisitionBatch, CancellationToken, Task> publish, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(physicalPages);
        ArgumentNullException.ThrowIfNull(publish);
        var inventory = new List<ClassicAspxInventory>();
        var evidence = new List<ClassicPageDiscovery>();
        await foreach (var observation in physicalPages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (observation.RowType != PhysicalPageRowType)
            {
                continue;
            }
            if (observation.ScanId != context.ScanId || observation.SiteCollectionId != context.SiteCollectionId ||
                !HasNonEmptyGuid(observation.FileUniqueId) || !TryNormalizePhysicalFilePath(observation.Url, out var path) ||
                !IsAspxPath(path))
            {
                evidence.Add(CreateIdentityGap(observation));
            }
            else
            {
                inventory.Add(ProjectPhysicalFile(observation, path));
            }
            if (inventory.Count + evidence.Count >= context.BatchSize)
            {
                await publish(new ClassicAspxAcquisitionBatch(inventory.ToArray(), evidence.ToArray()), cancellationToken).ConfigureAwait(false);
                inventory.Clear();
                evidence.Clear();
            }
        }
        if (inventory.Count + evidence.Count != 0)
        {
            await publish(new ClassicAspxAcquisitionBatch(inventory.ToArray(), evidence.ToArray()), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Copies server-observed file facts; Web/List/Folder values are supplementary observations, never a file identity substitute.</summary>
    private ClassicAspxInventory ProjectPhysicalFile(ClassicPageDiscovery observation, string path)
    {
        return new ClassicAspxInventory
        {
            ScanId = context.ScanId,
            AspxKey = ClassicAspxInventory.CreateTenantFileKey(context.SiteCollectionId, observation.FileUniqueId.Value),
            SiteCollectionId = context.SiteCollectionId,
            FileUniqueId = observation.FileUniqueId.Value,
            WebId = HasNonEmptyGuid(observation.WebId) ? observation.WebId : null,
            ListId = HasNonEmptyGuid(observation.ListId) ? observation.ListId : null,
            FolderUniqueId = HasNonEmptyGuid(observation.FolderUniqueId) ? observation.FolderUniqueId : null,
            SiteUrl = context.SiteUrl.AbsoluteUri,
            WebUrl = context.WebUrl.AbsoluteUri,
            Url = new Uri(context.SiteUrl, path).AbsoluteUri,
            FileName = observation.FileName ?? Uri.UnescapeDataString(path.Split('/')[^1]),
            ContentTypeId = observation.ContentTypeId,
            CustomizedPageStatus = observation.CustomizedPageStatus,
            SetupPath = observation.SetupPath,
            FileReadState = DiscoveryTerminalOutcome.Complete.ToString(),
            ProvenanceJson = JsonSerializer.Serialize(new[] { new
            {
                source = "FolderFileObservation",
                observation.RecordKey,
                observation.ObservationMethod,
                observation.DiscoveryStatus,
                observation.ErrorCodes
            }}),
            CoverageState = observation.DiscoveryStatus is "Discovered" or "Complete" or "Empty"
                ? ClassicAspxCoverageStates.Observed : ClassicAspxCoverageStates.Unknown,
            FirstObservedAtUtc = observation.ObservedAtUtc,
            ObservedAtUtc = observation.ObservedAtUtc
        };
    }

    /// <summary>
    /// Encodes decoded physical folder/file segments without reinterpreting literal percent escapes or hash characters.
    /// ResourcePath identity remains distinct from reference-URL navigation and stays within the observed site authority.
    /// </summary>
    private bool TryNormalizePhysicalFilePath(string locator, out string path)
    {
        return urlPolicy.TryNormalizeDecodedPhysicalPath(locator, out path);
    }

    /// <summary>Retains an invalid physical observation as a gap without deriving a replacement GUID or silently admitting another scope.</summary>
    private ClassicPageDiscovery CreateIdentityGap(ClassicPageDiscovery observation)
    {
        return new ClassicPageDiscovery
        {
            ScanId = context.ScanId,
            RecordKey = "aspx:Gap:" + DiscoveryHash.Of(context.WebId.ToString("D"), observation.RecordKey, UnprovedPhysicalIdentity),
            RowType = "Gap",
            ScopeType = InventoryGapScope,
            SiteUrl = observation.SiteUrl ?? context.SiteUrl.AbsoluteUri,
            WebUrl = observation.WebUrl,
            SiteCollectionId = context.SiteCollectionId,
            WebId = context.WebId,
            Url = observation.Url,
            DiscoveryStatus = DiscoveryTerminalOutcome.Unknown.ToString(),
            ErrorCodes = UnprovedPhysicalIdentity,
            ObservationMethod = nameof(ClassicAspxWebAcquisition),
            ObservedAtUtc = observation.ObservedAtUtc
        };
    }
}
