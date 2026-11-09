using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;

namespace PnP.Scanning.Core.Pipeline.Collection.List;

internal static class ClassicBlogContentReader
{
    internal static async Task<ClassicBlogListScopeSource> ReadListsAsync(PnPContext pnp,
        ClassicPageWebSource web, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // List reloads replace SDK metadata needed by the other mixed Classic components.
        using var contentContext = await pnp.CloneAsync();
        await contentContext.Web.LoadAsync(w => w.Lists.QueryProperties(l => l.Id, l => l.Title,
            l => l.TemplateType, l => l.BaseType, l => l.DefaultDisplayFormUrl,
            l => l.ContentTypes.QueryProperties(type => type.StringId, type => type.DisplayFormUrl),
            l => l.RootFolder.QueryProperties(f => f.ServerRelativeUrl)));
        token.ThrowIfCancellationRequested();
        var lists = contentContext.Web.Lists.AsRequested()
            .Select(l => new ClassicBlogListSource(l.Id, l.Title, l.RootFolder.ServerRelativeUrl,
                l.BaseType == ListBaseType.DocumentLibrary ? ClassicPageRules.DelveBlogPage :
                    l.TemplateType == ListTemplateType.Posts ? ClassicPageRules.BlogPage : ClassicPageRules.ASPXPage,
                l.BaseType == ListBaseType.DocumentLibrary ? null : NormalizeDisplayForm(l.DefaultDisplayFormUrl, pnp.Uri, web.SiteUrl),
                l.BaseType == ListBaseType.DocumentLibrary ? null : l.ContentTypes.AsRequested()
                    .Select(type => new ClassicContentTypeForm(type.StringId,
                        NormalizeDisplayForm(type.DisplayFormUrl, pnp.Uri, web.SiteUrl)!))
                    .Where(type => type.FormUrl != null).OrderByDescending(type => type.ContentTypeId.Length).ToArray()))
            .Where(list => list.PageType != ClassicPageRules.ASPXPage || LegacyDisplayFormAssessment.IsContentPageForm(list.DisplayFormUrl) ||
                list.ContentTypeForms?.Any(type => LegacyDisplayFormAssessment.IsContentPageForm(type.FormUrl)) == true)
            .OrderBy(l => l.ListId).ToArray();
        return new(web.SiteUrl, web.WebUrl, lists, SourceReadState.Complete);
    }

    private static string? NormalizeDisplayForm(string? value, Uri web, string siteUrl)
    {
        var site = new Uri(siteUrl);
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            !SharePointAspxUrlPolicy.HasSameHttpsAuthority(site, absolute))
        {
            Serilog.Log.Warning("A Blog display-form declaration has a foreign authority; its content URL remains unknown");
            return null;
        }
        return DisplayFormUrlPolicy.ToServerRelative(value!, web.AbsolutePath, site.AbsolutePath);
    }

    internal static async Task<ClassicPageBlogBatchSource> ReadBatchAsync(PnPContext pnp,
        ClassicPageWebSource web, ClassicBlogListSource expected, string? paging,
        bool skipUsers, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var contentContext = await pnp.CloneAsync();
        var list = await contentContext.Web.Lists.GetByIdAsync(expected.ListId, l => l.Id,
            l => l.Title, l => l.TemplateType, l => l.BaseType,
            l => l.RootFolder.QueryProperties(f => f.ServerRelativeUrl));
        token.ThrowIfCancellationRequested();
        if (!string.Equals(list.RootFolder.ServerRelativeUrl, expected.ListUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected Blog/Delve list root changed after scope acquisition.");
        var delve = expected.PageType == ClassicPageRules.DelveBlogPage;
        if ((!delve && expected.PageType == ClassicPageRules.BlogPage && list.TemplateType != ListTemplateType.Posts) ||
            (!delve && list.BaseType == ListBaseType.DocumentLibrary) ||
            (delve && list.BaseType != ListBaseType.DocumentLibrary))
            throw new InvalidDataException("The Blog/Delve source type differs from its selected list.");
        var query = ClassicPageQuery.Create(new(), false, null, skipUsers, delve);
        var batch = await ClassicPageOnlineSource.LoadBatchAsync(list, query, paging, token, web.SiteUrl, web.WebUrl);
        return batch with { DisplayFormUrl = expected.DisplayFormUrl, PageType = expected.PageType, ContentTypeForms = expected.ContentTypeForms };
    }
}
