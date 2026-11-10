#nullable enable
using System.Text.Json;
using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;

namespace PnP.Scanning.Core.Pipeline.Collection.File;

/// <summary>
/// Retains native identities read from an exact Web.Lists[id].Items[id].File relationship, not inferred from a locator.
/// </summary>
internal sealed record NativeItemFile(
    Guid SiteCollectionId,
    Guid WebId,
    Guid ListId,
    int ListItemId,
    Guid FileUniqueId,
    string Name,
    string ServerRelativeUrl,
    long Length,
    string CustomizedPageStatus);

/// <summary>
/// Acquires original item-to-file relationships in bounded CSOM batches with per-item failures and linked cancellation.
/// </summary>
internal static class NativeFileAssociationReader
{
    private const int BatchSize = 64;

    /// <summary>
    /// Reads selected items by native list and item IDs and verifies the returned site, Web, list, item, and file identities.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<int, FileItemAssociation>> ReadAsync(
        ClientContext context,
        Guid assessmentId,
        WebFacts web,
        Guid listId,
        IReadOnlyList<ListItemFacts> items,
        CancellationToken token)
    {
        Dictionary<int, FileItemAssociation> associations = [];
        Microsoft.SharePoint.Client.List list = context.Web.Lists.GetById(listId);
        foreach (ListItemFacts[] batch in items.DistinctBy(item => item.ListItemId).Chunk(BatchSize))
        {
            token.ThrowIfCancellationRequested();
            context.Load(context.Site, site => site.Id);
            context.Load(context.Web, owner => owner.Id);
            context.Load(list, owner => owner.Id);
            List<NativeFileRequest> requests = [];
            foreach (ListItemFacts item in batch)
            {
                if (item.ListItemId <= 0) throw new InvalidDataException("A native file link requires a real item ID.");
                ExceptionHandlingScope failure = new(context);
                Microsoft.SharePoint.Client.ListItem nativeItem = list.GetItemById(item.ListItemId);
                Microsoft.SharePoint.Client.File nativeFile = nativeItem.File;
                using (failure.StartScope())
                {
                    using (failure.StartTry())
                    {
                        context.Load(nativeItem, value => value.Id);
                        context.Load(
                            nativeFile,
                            value => value.UniqueId,
                            value => value.Name,
                            value => value.ServerRelativeUrl,
                            value => value.Length,
                            value => value.CustomizedPageStatus);
                    }

                    using (failure.StartCatch())
                    {
                    }
                }

                requests.Add(new(item, nativeItem, nativeFile, failure));
            }

            try
            {
                await context.ExecuteQueryAsync();
            }
            catch (Exception error) when (error is not OperationCanceledException && !token.IsCancellationRequested)
            {
                foreach (NativeFileRequest request in requests)
                    associations.Add(request.Item.ListItemId, new(ClassicContentReader.Failure(error), null));
                continue;
            }

            token.ThrowIfCancellationRequested();
            if (context.Site.Id != web.SiteCollectionId || context.Web.Id != web.WebId || list.Id != listId)
                throw new SnapshotIntegrityException(
                    "A native File association was returned by a foreign site, Web, or list.");
            foreach (NativeFileRequest request in requests)
            {
                if (request.Failure.HasException)
                {
                    associations.Add(
                        request.Item.ListItemId,
                        new(
                            new SourceReadState(
                                request.Failure.ServerErrorCode == -2147024891 ? "Denied" : "Failed",
                                request.Failure.ErrorMessage),
                            null));
                    continue;
                }

                try
                {
                    NativeItemFile observed = new(
                        context.Site.Id,
                        context.Web.Id,
                        list.Id,
                        request.NativeItem.Id,
                        request.NativeFile.UniqueId,
                        request.NativeFile.Name,
                        request.NativeFile.ServerRelativeUrl,
                        request.NativeFile.Length,
                        request.NativeFile.CustomizedPageStatus.ToString());
                    associations.Add(
                        request.Item.ListItemId,
                        FromNative(assessmentId, web, listId, request.Item, observed));
                }
                catch (Exception error) when (error is not OperationCanceledException && !token.IsCancellationRequested)
                {
                    associations.Add(request.Item.ListItemId, new(ClassicContentReader.Failure(error), null));
                }
            }
        }

        return associations;
    }

    /// <summary>
    /// Converts verified native relationship evidence into physical facts, retaining original item fields and explicitly unread content.
    /// </summary>
    internal static FileItemAssociation FromNative(
        Guid assessmentId,
        WebFacts web,
        Guid listId,
        ListItemFacts item,
        NativeItemFile observed)
    {
        if (assessmentId == Guid.Empty || observed.SiteCollectionId != web.SiteCollectionId ||
            observed.WebId != web.WebId || observed.ListId != listId || observed.ListItemId != item.ListItemId ||
            observed.SiteCollectionId == Guid.Empty || observed.WebId == Guid.Empty || observed.ListId == Guid.Empty ||
            observed.FileUniqueId == Guid.Empty || observed.ListItemId <= 0)
            throw new SnapshotIntegrityException(
                "The selected ListItem and its native File relationship have different or missing identities.");
        if (string.IsNullOrWhiteSpace(observed.ServerRelativeUrl) || !observed.ServerRelativeUrl.StartsWith('/') ||
            observed.ServerRelativeUrl.StartsWith("//", StringComparison.Ordinal))
            throw new SnapshotIntegrityException("The native File response has no physical server-relative path.");
        if (item.Fields.GetValueOrDefault("FileRef")?.Text is string fileRef && !string.Equals(
                fileRef,
                observed.ServerRelativeUrl,
                StringComparison.OrdinalIgnoreCase))
            throw new SnapshotIntegrityException(
                "Returned item and native File locators conflict; they cannot be silently associated.");
        DateTime observedAt = DateTime.UtcNow;
        Storage.ClassicPageDiscovery nativeIdentity = new()
        {
            ScanId = assessmentId,
            SiteUrl = web.SiteUrl,
            WebUrl = web.WebUrl,
            SiteCollectionId = observed.SiteCollectionId,
            WebId = observed.WebId,
            ListId = observed.ListId,
            ListItemId = observed.ListItemId,
            FileUniqueId = observed.FileUniqueId,
            Url = observed.ServerRelativeUrl,
            FileName = observed.Name,
            ObservedAtUtc = observedAt,
        };
        Storage.SPFile facts =
            SPFileFactProjection.FromItemMetadata(
                nativeIdentity,
                item.Fields.ToDictionary(field => field.Key, field => field.Value.ToValue())) ??
            throw new SnapshotIntegrityException("A verified native File relationship did not produce physical facts.");
        facts.Length = observed.Length;
        facts.SiteUrl = web.SiteUrl;
        facts.WebUrl = web.WebUrl;
        facts.ServerRelativeUrl = observed.ServerRelativeUrl;
        facts.FileName = observed.Name;
        facts.CustomizedPageStatus = observed.CustomizedPageStatus;
        facts.FileReadState = "Complete";
        facts.FirstObservedAtUtc = facts.ObservedAtUtc = observedAt;
        facts.ProvenanceJson = JsonSerializer.Serialize(
            new[]
            {
                new
                {
                    source = "NativeListItemFile",
                    observed.SiteCollectionId,
                    observed.WebId,
                    observed.ListId,
                    observed.ListItemId,
                    observed.FileUniqueId,
                    contentReadState = "NotAttempted"
                },
            });
        return new(SourceReadState.Complete, CollectionJson.Convert<PhysicalFileFacts>(facts));
    }

    /// <summary>
    /// Owns the native objects and per-item exception scope for one bounded relation request.
    /// </summary>
    private sealed record NativeFileRequest(
        ListItemFacts Item,
        Microsoft.SharePoint.Client.ListItem NativeItem,
        Microsoft.SharePoint.Client.File NativeFile,
        ExceptionHandlingScope Failure);
}