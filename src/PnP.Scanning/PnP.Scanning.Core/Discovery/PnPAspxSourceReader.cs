using PnP.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Uses the existing scheduled Web context and PnP physical-file download capability.</summary>
internal static class PnPAspxSourceReader
{
    internal static async Task<AspxSourceReadResult> ReadAsync(PnPContext context, Guid siteId, Guid webId,
        ClassicPageDiscovery row, CancellationToken token)
    {
        var discovery = row.DiscoveryObservation ?? AspxFileObservation.FromDiscovery(row);
        var version = new AspxSourceVersion(DateTimeOffset.UtcNow);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(discovery.Identity.Url))
            return AspxSourceReadResult.Unavailable(discovery, AspxSourceTransportState.NotAttempted, "SourcePathNotReturned");
        if (AspxFileIdentity.Present(discovery.Identity.SiteCollectionId) && discovery.Identity.SiteCollectionId != siteId ||
            AspxFileIdentity.Present(discovery.Identity.WebId) && discovery.Identity.WebId != webId)
            return AspxSourceReadResult.Unavailable(discovery, AspxSourceTransportState.NotAttempted,
                "DiscoveryIdentityOutsideScheduledSiteWeb");
        var identity = discovery.Identity;
        long? expectedLength = null;
        try
        {
            var file = await context.Web.GetFileByServerRelativeUrlAsync(discovery.Identity.Url,
                value => value.UniqueId, value => value.ServerRelativeUrl, value => value.Name,
                value => value.Length, value => value.ETag, value => value.MajorVersion,
                value => value.MinorVersion, value => value.ListId).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Never access unavailable value-type properties and mistake their defaults for observations.
            identity = new(siteId, webId,
                file.IsPropertyAvailable(value => value.UniqueId) && file.UniqueId != Guid.Empty ? file.UniqueId : null,
                file.IsPropertyAvailable(value => value.ServerRelativeUrl) ? file.ServerRelativeUrl : discovery.Identity.Url,
                file.IsPropertyAvailable(value => value.Name) ? file.Name : discovery.Identity.Name,
                file.IsPropertyAvailable(value => value.ListId) && file.ListId != Guid.Empty ? file.ListId : null,
                discovery.Identity.ListItemId);
            version = new(DateTimeOffset.UtcNow,
                file.IsPropertyAvailable(value => value.ETag) ? file.ETag : null,
                file.IsPropertyAvailable(value => value.MajorVersion) ? file.MajorVersion : null,
                file.IsPropertyAvailable(value => value.MinorVersion) ? file.MinorVersion : null);
            expectedLength = file.IsPropertyAvailable(value => value.Length) && file.Length >= 0 ? file.Length : null;
            if (!AspxFileIdentity.Present(identity.FileUniqueId))
                return AspxSourceReadResult.Unavailable(discovery, AspxSourceTransportState.NotReturned,
                    "SourceFileUniqueIdNotReturned", identity, version, expectedLength);
            if (AspxFileIdentity.Present(discovery.Identity.FileUniqueId) &&
                discovery.Identity.FileUniqueId != identity.FileUniqueId)
                return AspxSourceReadResult.Unavailable(discovery, AspxSourceTransportState.Failed,
                    "SourceFileIdentityChanged", identity, version, expectedLength);
            if (AspxFileIdentity.Present(discovery.Identity.ListId) && AspxFileIdentity.Present(identity.ListId) &&
                discovery.Identity.ListId != identity.ListId)
                return AspxSourceReadResult.Unavailable(discovery, AspxSourceTransportState.Failed,
                    "SourceListIdentityChanged", identity, version, expectedLength);
            return await AspxSourceReader.ReadAsync(discovery, identity, version, async ct =>
            {
                ct.ThrowIfCancellationRequested();
                // .NET PnP downloads by UniqueId via download.aspx, not a rendered ASPX navigation.
                // The inherited SDK API has no per-call CancellationToken; check it again upon return.
                return await file.GetContentAsync(true).ConfigureAwait(false);
            }, token, expectedLength).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            return AspxSourceReadResult.Unavailable(discovery,
                AspxSourceReader.IsDenied(ex) ? AspxSourceTransportState.Denied : AspxSourceTransportState.Failed,
                AssessmentWebDiscovery.ErrorCode(ex) + ":" + ex.Message, identity, version, expectedLength,
                AspxSourceReader.HttpStatus(ex));
        }
    }
}
