using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SharePoint.Client;
using PnP.Core.Services;
using PnP.Scanning.Core.Pipeline.Contracts.Page;

namespace PnP.Scanning.Core.Pipeline.Collection.Page;

internal sealed class AspxPageEnricher(PnPContext pnp, ClientContext csom, Uri site, Uri web)
{
    private sealed record ListContext(Guid Id, int Template, bool Catalog, string Root);
    private readonly Dictionary<Guid, ListContext> lists = [];
    private readonly HashSet<string> views = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> forms = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharePointAspxUrlPolicy urls = new(site, web);
    private bool metadataComplete;
    private bool rolesComplete;
    private bool rolesAttempted;

    internal async Task LoadRolesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        rolesAttempted = true;
        try
        {
            csom.Load(csom.Web.Lists, values => values.Include(
                l => l.Id, l => l.BaseTemplate, l => l.IsCatalog, l => l.RootFolder.ServerRelativeUrl,
                l => l.DefaultDisplayFormUrl, l => l.DefaultEditFormUrl, l => l.DefaultNewFormUrl));
            await csom.ExecuteQueryAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            metadataComplete = true;
            rolesComplete = true;
            foreach (var list in csom.Web.Lists)
            {
                lists.Add(list.Id, new(list.Id, list.BaseTemplate, list.IsCatalog, list.RootFolder.ServerRelativeUrl));
                Add(forms, list.DefaultDisplayFormUrl);
                Add(forms, list.DefaultEditFormUrl);
                Add(forms, list.DefaultNewFormUrl);
                try
                {
                    var knownUserInfo = list.BaseTemplate == 112 &&
                        list.RootFolder.ServerRelativeUrl.TrimEnd('/').EndsWith("/_catalogs/users", StringComparison.OrdinalIgnoreCase);
                    var usedDefaultForms = await ReadWithFormIdentityFallbackAsync(knownUserInfo, list.Id,
                        includeFormObjects => ReadListRegistrationsAsync(list, includeFormObjects, token), token)
                        .ConfigureAwait(false);
                    foreach (var view in list.Views) Add(views, view.ServerRelativeUrl);
                    if (usedDefaultForms)
                    {
                        Add(forms, list.DefaultDisplayFormUrl);
                        Add(forms, list.DefaultEditFormUrl);
                        Add(forms, list.DefaultNewFormUrl);
                        if (!HasCompleteDefaultForms(knownUserInfo, list.DefaultDisplayFormUrl,
                            list.DefaultEditFormUrl, list.DefaultNewFormUrl))
                        {
                            rolesComplete = false;
                            Serilog.Log.Warning(
                                "ASPX default form URLs recovered for list {ListId}, but full form registration coverage remains unknown",
                                list.Id);
                        }
                    }
                    else
                    {
                        foreach (var form in list.Forms) Add(forms, form.ServerRelativeUrl);
                    }
                    foreach (var type in list.ContentTypes)
                    {
                        Add(forms, type.DisplayFormUrl);
                        Add(forms, type.EditFormUrl);
                        Add(forms, type.NewFormUrl);
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    rolesComplete = false;
                    Serilog.Log.Warning(error, "ASPX View/Form registration read failed for list {ListId}", list.Id);
                }
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            metadataComplete = rolesComplete = false;
            Serilog.Log.Warning(error, "ASPX list-context acquisition failed for {WebUrl}", web);
        }
        token.ThrowIfCancellationRequested();
    }

    private async Task ReadListRegistrationsAsync(Microsoft.SharePoint.Client.List list, bool includeFormObjects,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        csom.Load(list.Views, values => values.Include(v => v.ServerRelativeUrl));
        csom.Load(list.ContentTypes, values => values.Include(
            c => c.DisplayFormUrl, c => c.EditFormUrl, c => c.NewFormUrl));
        if (includeFormObjects)
            csom.Load(list.Forms, values => values.Include(f => f.ServerRelativeUrl));
        else
            csom.Load(list, l => l.DefaultDisplayFormUrl, l => l.DefaultEditFormUrl, l => l.DefaultNewFormUrl);
        await csom.ExecuteQueryAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    internal static async Task<bool> ReadWithFormIdentityFallbackAsync(bool knownUserInfo, Guid listId,
        Func<bool, Task> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (knownUserInfo)
        {
            await read(false).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return true;
        }
        try
        {
            await read(true).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception error) when (IsNullGuidParameter(error))
        {
            token.ThrowIfCancellationRequested();
            Serilog.Log.Warning(error,
                "ASPX form identity read failed for list {ListId}; retrying once using default form URLs without Form object identities",
                listId);
            await read(false).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return true;
        }
    }

    internal static bool HasCompleteDefaultForms(bool knownUserInfo, string? display, string? edit, string? create) =>
        knownUserInfo && !string.IsNullOrWhiteSpace(display) &&
        !string.IsNullOrWhiteSpace(edit) && !string.IsNullOrWhiteSpace(create);

    internal static bool IsNullGuidParameter(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            if (current is ArgumentNullException argument && argument.ParamName == "g")
                return true;
            if (current is ServerException server && IsNullGuidServerError(server.ServerErrorTypeName, server.Message))
                return true;
        }
        return false;
    }

    internal static bool IsNullGuidServerError(string? errorTypeName, string? message) =>
        errorTypeName == typeof(ArgumentNullException).FullName && message != null &&
        Regex.IsMatch(message, @"(?:^|\r?\n)\s*Parameter name:\s*g\s*(?:\r?\n|$)|\(Parameter ['""]g['""]\)",
            RegexOptions.CultureInvariant);

    private void Add(HashSet<string> targets, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (TryNormalizeRegistration(site, web, value, out var normalized))
            targets.Add(normalized);
        else
            rolesComplete = false;
    }

    internal static bool TryNormalizeRegistration(Uri site, Uri web, string value, out string normalized)
    {
        normalized = "";
        if (!value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            !SharePointAspxUrlPolicy.HasSameHttpsAuthority(site, absolute))
            return false;
        var path = DisplayFormUrlPolicy.ToServerRelative(value, web.AbsolutePath, site.AbsolutePath);
        return path != null && new SharePointAspxUrlPolicy(site, web).TryNormalizeResourceReference(path, out normalized);
    }

    internal async Task<string> ReadAsync(SPFileSource file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ListContext? owner = null;
        if (file.ListId is { } id) lists.TryGetValue(id, out owner);
        if (owner == null && file.ServerRelativeUrl != null)
            owner = lists.Values.Where(l => SharePointAspxUrlPolicy.IsPathWithinRoot(file.ServerRelativeUrl, l.Root))
                .OrderByDescending(l => l.Root.Length).FirstOrDefault();
        var normalized = urls.TryNormalizeDecodedPhysicalPath(file.ServerRelativeUrl, out var path) ? path : null;
        bool? view = normalized != null && views.Contains(normalized) ? true : rolesComplete ? false : null;
        bool? form = normalized != null && forms.Contains(normalized) ? true : rolesComplete ? false : null;
        bool? catalog = owner?.Catalog ?? (metadataComplete && file.ListId == null ? false : null);
        var roleState = !rolesAttempted ? "NotAttempted" : rolesComplete && catalog != null ? "Complete" : "Partial";
        try
        {
            var source = await pnp.Web.GetFileByIdAsync(file.FileUniqueId, f => f.UniqueId, f => f.Length).ConfigureAwait(false);
            if (source.UniqueId != file.FileUniqueId)
                throw new InvalidDataException("ASPX source response has a different file identity.");
            var observed = await AspxSourceReader.ReadAsync(source, token).ConfigureAwait(false);
            return JsonSerializer.Serialize(new AspxPageEvidence(
                observed.State, null, null, "PendingAnalysis",
                null, observed.Error, owner?.Template,
                catalog, view, form, roleState, DateTime.UtcNow, observed.Bytes));
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            Serilog.Log.Warning(error, "ASPX source read failed for file {FileId}", file.FileUniqueId);
            return JsonSerializer.Serialize(new AspxPageEvidence(
                AssessmentWebDiscovery.Status(AssessmentWebDiscovery.Classify(error)), null, null, "Unresolved",
                null, AssessmentWebDiscovery.ErrorDetail(error), owner?.Template,
                catalog, view, form, roleState, DateTime.UtcNow));
        }
    }
}
