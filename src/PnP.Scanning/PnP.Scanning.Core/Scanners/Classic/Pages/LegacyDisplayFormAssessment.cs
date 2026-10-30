using System.Globalization;
using System.Text.Json;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

/// <summary>
/// Assesses legacy item-only pages rendered through custom display forms. These assessment rows are
/// not physical files: this component never creates discovery inventory, file identities or bindings.
/// </summary>
internal static class LegacyDisplayFormAssessment
{
    private const string ContentTypeIdField = "ContentTypeId";
    private const string TitleField = "Title";
    private const string ModifiedField = "Modified";
    private const string EditorField = "Editor";
    private const string StockDisplayFormName = "DispForm.aspx";
    private const string LayoutsPath = "/_layouts/";
    private const string NextHrefProperty = "NextHref";
    private const int PageSize = 1000;

    /// <summary>
    /// Reads only eligible non-document lists using the scanner's already loaded metadata. HOME
    /// assessment never enumerates item-only pages, preserving its physical welcome-page-only scope.
    /// Content-type overrides take precedence over the list form, with the longest matching ID first.
    /// Failed lists retain already assessed items and report failure outside the acquisition handler.
    /// </summary>
    internal static async Task<List<ClassicPage>> ReadAsync(Guid scanId, string siteUrl, string webUrl,
        Uri contextUri, IReadOnlyList<IList> lists, bool homePageOnly, bool skipUserInformation,
        CancellationToken cancellationToken, Func<ClassicPageDiscovery, Exception, Task> onListFailure)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pages = new List<ClassicPage>();
        if (homePageOnly)
        {
            return pages;
        }

        string webRoot = contextUri.AbsolutePath;
        string siteRoot = Uri.TryCreate(siteUrl, UriKind.Absolute, out var siteUri)
            ? siteUri.AbsolutePath
            : webRoot;

        for (int index = 0; index < lists.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = lists[index];
            Guid? listId = null;
            int previousPageCount = pages.Count;
            string stage = "LegacyDisplayFormMetadata";
            Exception failure = null;
            try
            {
                listId = list.Id;
                if (list.BaseType == ListBaseType.DocumentLibrary)
                {
                    continue;
                }

                var contentTypeForms = list.ContentTypes.AsRequested()
                    .Where(contentType => !string.IsNullOrEmpty(contentType.StringId))
                    .Select(contentType => (ContentTypeId: contentType.StringId, FormUrl: DisplayFormUrlPolicy.ToServerRelative(
                        contentType.DisplayFormUrl, webRoot, siteRoot)))
                    .Where(candidate => candidate.FormUrl != null)
                    .OrderByDescending(candidate => candidate.ContentTypeId.Length)
                    .ToList();
                string listForm = DisplayFormUrlPolicy.ToServerRelative(list.DefaultDisplayFormUrl, webRoot, siteRoot);
                if (!contentTypeForms.Select(candidate => candidate.FormUrl).Append(listForm).Any(IsContentPageForm))
                {
                    continue;
                }

                stage = "LegacyDisplayFormItems";
                await ReadItemsAsync(list, skipUserInformation, cancellationToken, items =>
                {
                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string form = ResolveDisplayForm(contentTypeForms, listForm,
                            GetFieldValue(item, ContentTypeIdField, string.Empty));
                        if (!IsContentPageForm(form))
                        {
                            continue;
                        }

                        pages.Add(AssessItem(scanId, siteUrl, webUrl, list, item, form, skipUserInformation));
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }

            if (failure != null)
            {
                var evidence = new ClassicPageDiscovery
                {
                    ScanId = scanId,
                    SiteUrl = siteUrl,
                    WebUrl = webUrl,
                    RecordKey = "assessment:legacy-display-form:" + DiscoveryHash.Of(siteUrl, webUrl,
                        listId?.ToString("D") ?? "index:" + index.ToString(CultureInfo.InvariantCulture)),
                    RowType = "Assessment",
                    ScopeType = "List",
                    ListId = listId,
                    Url = contextUri.AbsoluteUri,
                    ObservationMethod = nameof(LegacyDisplayFormAssessment),
                    DiscoveryStatus = "NotApplicable",
                    AssessmentStatus = "Failed",
                    ObservedAtUtc = DateTime.UtcNow,
                    EvidenceJson = JsonSerializer.Serialize(new
                    {
                        outcome = AssessmentWebDiscovery.Status(AssessmentWebDiscovery.Classify(failure)),
                        retainedPageCount = pages.Count - previousPageCount,
                    }),
                };
                AssessmentWebDiscovery.AddError(evidence, stage, AssessmentWebDiscovery.ErrorCode(failure),
                    AssessmentWebDiscovery.ErrorDetail(failure));
                // Persistence failures must propagate, not be classified as another unreadable list.
                await onListFailure(evidence, failure).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return pages;
    }

    /// <summary>
    /// Retains the legacy stock-form heuristic: a DispForm.aspx leaf or any _layouts form renders
    /// ordinary list data, not a separately assessed content page. Custom forms keeping the stock
    /// filename remain excluded, matching the existing assessment's documented limitation.
    /// </summary>
    private static bool IsContentPageForm(string form)
    {
        if (form == null)
        {
            return false;
        }

        string path = form.Split('?')[0];
        return !path.Contains(LayoutsPath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetFileName(path), StockDisplayFormName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves the most-specific declared content-type form before the list-level fallback.</summary>
    private static string ResolveDisplayForm(List<(string ContentTypeId, string FormUrl)> contentTypeForms,
        string listForm, string itemContentTypeId)
    {
        foreach (var candidate in contentTypeForms)
        {
            if (itemContentTypeId.StartsWith(candidate.ContentTypeId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.FormUrl;
            }
        }

        return listForm;
    }

    /// <summary>
    /// Projects one item into the legacy report model without requesting a File surface or assigning
    /// FileUniqueId. Its web parts belong to the shared form and are not per-item enrichment inputs.
    /// </summary>
    private static ClassicPage AssessItem(Guid scanId, string siteUrl, string webUrl, IList list,
        IListItem item, string form, bool skipUserInformation)
    {
        string pageUrl = form + "?ID=" + item.Id.ToString(CultureInfo.InvariantCulture);
        bool blogPost = list.TemplateType == ListTemplateType.Posts;
        return new ClassicPage
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            PageUrl = pageUrl,
            PageName = GetFieldValue(item, TitleField, string.Empty),
            ListUrl = list.RootFolder.ServerRelativeUrl,
            ListTitle = list.Title,
            ListId = list.Id,
            ListItemId = item.Id,
            ModifiedAt = GetFieldValue<DateTime>(item, ModifiedField),
            ModifiedBy = PageScanComponent.GetModifiedBy(item.Values, skipUserInformation),
            PageType = blogPost ? PageScanComponent.BlogPage : PageScanComponent.ASPXPage,
            // An item address is not the physical welcome page, even if its shared form is.
            HomePage = false,
            DiscoveryStatus = "Discovered",
            AssessmentStatus = "Complete",
            RemediationCode = (blogPost ? RemediationCodes.CP4 : RemediationCodes.CP5).ToString(),
        };
    }

    /// <summary>
    /// Pages through the native list stream, clearing cached items before each request and honoring
    /// cancellation between pages. Only assessment fields are requested: no file identity or content.
    /// </summary>
    private static async Task ReadItemsAsync(IList list, bool skipUserInformation,
        CancellationToken cancellationToken, Action<IEnumerable<IListItem>> processItems)
    {
        string paging = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            list.Items.Clear();
            var output = await list.LoadListDataAsStreamAsync(new RenderListDataOptions
            {
                ViewXml = ItemQuery(skipUserInformation),
                RenderOptions = RenderListDataOptionsFlags.ListData,
                Paging = paging,
            }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            processItems(list.Items.AsRequested());
            paging = output.TryGetValue(NextHrefProperty, out var nextHref)
                ? nextHref?.ToString()?.TrimStart('?')
                : null;
        }
        while (!string.IsNullOrEmpty(paging));
    }

    /// <summary>
    /// Builds the legacy recursive item CAML without an ASPX-file filter, retaining paged ID order
    /// while omitting Editor when user information is disabled.
    /// </summary>
    private static string ItemQuery(bool skipUserInformation)
    {
        return $@"<View Scope='RecursiveAll'>
  <ViewFields>
    <FieldRef Name='ID' />
    <FieldRef Name='{ContentTypeIdField}' />
    <FieldRef Name='{TitleField}' />
    <FieldRef Name='{ModifiedField}' />
    {(skipUserInformation ? string.Empty : $"<FieldRef Name='{EditorField}' />")}
  </ViewFields>
  <OrderBy Override='TRUE'><FieldRef Name='ID' Ascending='FALSE' /></OrderBy>
  <RowLimit Paged='TRUE'>{PageSize}</RowLimit>
</View>";
    }

    /// <summary>Reads typed native list-stream values, preserving the legacy string conversion fallback.</summary>
    private static T GetFieldValue<T>(IListItem item, string name, T defaultValue = default)
    {
        if (item.Values.TryGetValue(name, out var value) && value != null)
        {
            if (value is T typed)
            {
                return typed;
            }

            if (typeof(T) == typeof(string))
            {
                return (T)(object)Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        return defaultValue;
    }
}
