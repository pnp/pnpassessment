using PnP.Core.Model;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

/// <summary>Loads assessment metadata once and shares a proven complete list response with discovery.</summary>
internal static class ClassicAssessmentMetadata
{
    internal static async Task<SharePointListMetadataSnapshot> LoadAsync(PnPContext context,
        PnPContextOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mapping = (IDataModelMappingHandler)context.Web;
        var previousMapping = mapping.PostMappingHandler;
        var previousGraphFirst = context.GraphFirst;
        SharePointListMetadataSnapshot sharedLists = null;
        var listResponses = 0;
        mapping.PostMappingHandler = json =>
        {
            previousMapping?.Invoke(json);
            if (!json.Contains("\"Lists\"", StringComparison.Ordinal)) return;
            listResponses++;
            // Do not infer completeness from Requested, AsRequested, or the observed item count.
            // The snapshot parser checks the actual response and rejects continuation/partial data.
            sharedLists = listResponses == 1 ? SharePointListMetadataSnapshot.TryCreate(context.Uri, json) : null;
        };
        try
        {
            // This is the same SharePoint metadata projection used during context initialization,
            // now loaded into the existing identity context instead of creating another context.
            context.GraphFirst = false;
            var batch = context.NewBatch();
            if (options.AdditionalSitePropertiesOnCreate?.Any() == true)
                await context.Site.LoadBatchAsync(batch, options.AdditionalSitePropertiesOnCreate.ToArray()).ConfigureAwait(false);
            if (options.AdditionalWebPropertiesOnCreate?.Any() == true)
                await context.Web.LoadBatchAsync(batch, options.AdditionalWebPropertiesOnCreate.ToArray()).ConfigureAwait(false);
            await context.ExecuteAsync(batch).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return sharedLists;
        }
        finally
        {
            mapping.PostMappingHandler = previousMapping;
            context.GraphFirst = previousGraphFirst;
        }
    }

    internal static async Task<List<ClassicPageDiscovery>> AcquirePagesAsync(bool homePageOnly,
        Func<Task<SharePointListMetadataSnapshot>> loadMetadata,
        Func<SharePointListMetadataSnapshot, Task<List<ClassicPageDiscovery>>> discover)
    {
        // A home-page scan must retain its targeted discovery path, not preload an all-lists authority.
        if (homePageOnly)
        {
            var pages = await discover(null).ConfigureAwait(false);
            await loadMetadata().ConfigureAwait(false);
            return pages;
        }
        var sharedLists = await loadMetadata().ConfigureAwait(false);
        return await discover(sharedLists).ConfigureAwait(false);
    }
}
