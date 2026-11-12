using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Collection.Module;
using PnP.Scanning.Core.Discovery;
#nullable enable
using Microsoft.SharePoint.Client;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Collection.List;
using PnP.Scanning.Core.ClassicPages.Collection.File;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using SdkList = PnP.Core.Model.SharePoint.IList;


namespace PnP.Scanning.Core.ClassicPages.Collection.Page;

/// <summary>Reads detached file-page metadata and optional web-part inputs without performing readiness analysis.</summary>
internal static class FilePageSourceReader
{
    /// <summary>
    /// Captures physical page inputs without performing detached readiness analysis.
    /// </summary>
    internal static async Task<FilePageEvidence> ReadFileEnrichmentInputsAsync(
        PnPContext pnp,
        ClientContext csom,
        WebFacts web,
        DiscoveryEvidence page,
        bool skipUsers,
        CancellationToken token)
    {
        SdkList? list = pnp.Web.Lists.AsRequested().FirstOrDefault(x => x.Id == page.ListId);
        if (list == null || page.ListItemId == null)
        {
            string[]? roots = pnp.Web.Lists.AsRequested().Select(x => x.RootFolder.ServerRelativeUrl).ToArray();
            FilePageEvidence? input = UnavailableFileEnrichment(web, page, SourceReadState.NotAttempted) with
            {
                FileLevelOnly = true, ListRootFolderUrls = roots, OwningListMatched = list != null
            };
            if (FileLevelPageProjection.FileLevelPageType(SetupPathClassifier.Classify(page.SetupPath)) == null)
                return input;
            try
            {
                (Dictionary<string, SourceField>? fileProperties, ClassicPageWebPartSource[]? fileParts) =
                    await PageWebPartSourceReader.ReadAsync(
                        csom,
                        page.Url,
                        ClassicPageRules.WebPartPage,
                        input.Fields,
                        token);
                return input with
                {
                    FileProperties = fileProperties, WebParts = fileParts, WebPartsState = SourceReadState.Complete
                };
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                return input with { WebPartsState = ClassicContentReader.Failure(error) };
            }
        }

        FilePageEvidence? metadata = await ReadFileItemMetadataAsync(list, page, skipUsers, token);
        if (!metadata.MetadataState.Succeeded) return metadata;
        Dictionary<string, SourceField>? fields = metadata.Fields;
        string? type =
            ClassicPageRules.IsPublishingContentType(
                fields.GetValueOrDefault("ContentTypeId")?.Text ?? page.ContentTypeId)
                ? ClassicPageRules.PublishingPage
                : ClassicPageRules.GetPageType(fields.ToDictionary(x => x.Key, x => x.Value.ToValue()));
        Dictionary<string, SourceField>? properties = new Dictionary<string, SourceField>();
        ClassicPageWebPartSource[] parts = [];
        SourceReadState? partsState = SourceReadState.NotAttempted;
        if (type is ClassicPageRules.WikiPage or ClassicPageRules.WebPartPage or ClassicPageRules.PublishingPage)
        {
            try
            {
                (properties, parts) = await PageWebPartSourceReader.ReadAsync(csom, page.Url, type, fields, token);
                partsState = SourceReadState.Complete;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                partsState = ClassicContentReader.Failure(error);
            }
        }

        string? display = null;
        SourceReadState? fallback = SourceReadState.NotAttempted;
        if (!web.CanModernizeState.Succeeded && HomePageDetector.IsHomePage(page.Url, web.WelcomePage) == true)
        {
            try
            {
                Microsoft.SharePoint.Client.ListItem? item = csom.Web.GetFileByServerRelativeUrl(page.Url)
                    .ListItemAllFields;
                csom.Load(item.ContentType, ct => ct.DisplayFormTemplateName);
                await csom.ExecuteQueryAsync();
                display = item.ContentType.DisplayFormTemplateName;
                fallback = SourceReadState.Complete;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                fallback = ClassicContentReader.Failure(error);
            }
        }

        token.ThrowIfCancellationRequested();
        return metadata with
        {
            FileProperties = properties,
            WebParts = parts,
            WebPartsState = partsState,
            ContentTypeDisplayFormTemplateName = display,
            HomeFallbackState = fallback
        };
    }

    /// <summary>
    /// Reads the exact discovered list item and retains returned identity and explicit metadata failure.
    /// </summary>
    internal static async Task<FilePageEvidence> ReadFileItemMetadataAsync(
        SdkList list,
        DiscoveryEvidence page,
        bool skipUsers,
        CancellationToken token)
    {
        if (page.ListId != list.Id || page.ListItemId == null || page.ListItemId <= 0)
            throw new InvalidDataException(
                "Physical page metadata requires its discovered list and list-item identity.");

        ListItemFieldBatch batch = await ListItemContentReader.LoadItemFieldsAsync(
            list,
            ListItemQueryBuilder.ForFileItem(page.ListItemId.Value, skipUsers),
            null,
            token);
        SourceReadState? state = SourceReadState.Complete;
        Dictionary<string, SourceField>? fields = new Dictionary<string, SourceField>();
        if (batch.Items.Length != 1 || batch.NextPage != null ||
            !batch.Items[0].TryGetValue("ID", out SourceField? id) || id.Text != page.ListItemId.Value.ToString())
            state = new("NotReturned", "The discovered list item was not returned uniquely.");
        else
        {
            fields = batch.Items[0];
            ClassicPageRules.ResolvePhysicalPageUrl(fields.ToDictionary(x => x.Key, x => x.Value.ToValue()), page.Url);
        }

        PhysicalFileFacts? fileFacts = state.Succeeded
            ? PhysicalFileProjection.FromItemMetadata(page, fields.ToDictionary(x => x.Key, x => x.Value.ToValue()))
            : PhysicalFileProjection.FromItemFailure(page, new InvalidDataException(state.Error ?? state.Status));
        return new(
            page.SiteUrl,
            page.WebUrl,
            page.RecordKey,
            batch.ListId,
            batch.ListTitle,
            batch.ListUrl,
            fields,
            new(),
            [],
            state,
            SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            fileFacts,
            ReturnedItems: batch.Items);
    }

    /// <summary>
    /// Constructs an unavailable page input without inventing successful metadata or web-part acquisition.
    /// </summary>
    internal static FilePageEvidence UnavailableFileEnrichment(
        WebFacts web,
        DiscoveryEvidence page,
        SourceReadState state)
    {
        PhysicalFileFacts? facts = state.Status is "Failed" or "Denied" or "NotReturned"
            ? PhysicalFileProjection.FromItemFailure(page, new InvalidDataException(state.Error ?? state.Status))
            : null;
        return new(
            web.SiteUrl,
            web.WebUrl,
            page.RecordKey,
            page.ListId,
            null,
            null,
            new(),
            new(),
            [],
            state,
            SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            facts);
    }
}