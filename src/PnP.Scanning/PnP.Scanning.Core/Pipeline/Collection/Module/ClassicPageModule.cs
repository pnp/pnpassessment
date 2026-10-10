#nullable enable
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Site;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

internal static class ClassicPageModule
{
    internal const string Key = "classicpage";
    internal const string InputVersion = "classicpage-source-v1";
    internal const string RuleVersion = "classicpage-v1";
    internal static ModuleRegistry Registry(Func<CollectionContext, CancellationToken, Task<IClassicPageOnlineSource>>? online = null) => new(
        new[] { new CollectionRegistration(Key, InputVersion, () => new ClassicPageCollectionModule(online), ValidateParameters, ValidateOptions) },
        new[] { new AnalysisRegistration(Key, RuleVersion, new[] { InputVersion }, () => new ClassicPageAnalysisModule(),
            ClassicPageRuleMetadata.Validate, ClassicPageRuleMetadata.Pin) },
        new Dictionary<string, string> { [Key] = RuleVersion });
    internal static void ValidateParameters(VersionedJson parameters)
    {
        if (parameters.SchemaVersion != 1 || parameters.Value.ValueKind != System.Text.Json.JsonValueKind.Object || parameters.Value.EnumerateObject().Any())
            throw new ArgumentException("Classic Pages v1 uses its typed CLI page options; module parameters must be empty.");
    }
    internal static void ValidateOptions(StartRequest request)
    {
        if (!request.Mode.Equals(Mode.ClassicPage.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The classicpage module requires --mode classicpage.");
        if (!Guid.TryParse(request.ApplicationId, out var application) || application == Guid.Empty)
            throw new ArgumentException("A nonempty application ID is required for Classic Page collection.");
        if (string.IsNullOrWhiteSpace(request.Tenant)) throw new ArgumentException("A tenant is required for Classic Page collection.");
        if (!Enum.TryParse<Authentication.AuthenticationMode>(request.AuthMode, true, out _) ||
            !Enum.TryParse<global::PnP.Core.Services.Microsoft365Environment>(request.Environment, true, out _))
            throw new ArgumentException("A supported authentication mode and cloud environment are required.");
        foreach (var component in Enum.GetValues<ClassicComponent>().Where(x => x != ClassicComponent.Pages))
            if (request.Properties.Any(x => x.Property == component.ToString() && bool.TryParse(x.Value, out var enabled) && enabled))
                throw new ArgumentException($"Classic Pages cannot select component {component}.");
        var options = Options(request);
        if (request.Properties.GroupBy(x => x.Property, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new ArgumentException("Collection property names must be unique.");
        if (options.AuditLogWindowDays is < 1 or > 180) throw new ArgumentException("Audit log window must be between 1 and 180 days.");
    }
    internal static ClassicPageSourceOptions Options(StartRequest request)
    {
        bool Flag(string key) => request.Properties.FirstOrDefault(x => x.Property == key)?.Value is string value && bool.Parse(value);
        var window = request.Properties.FirstOrDefault(x => x.Property == Constants.StartClassicAuditLogWindowDays)?.Value;
        return new(Flag(Constants.StartClassicExportWebPartProperties), Flag(Constants.StartClassicSkipUsageInformation),
            Flag(Constants.StartClassicSkipUserInformation), Flag(Constants.StartClassicHomePageOnly), window == null ? 14 : int.Parse(window));
    }
}

internal sealed class ClassicPageCollectionModule(Func<CollectionContext, CancellationToken, Task<IClassicPageOnlineSource>>? online = null)
    : ICollectionModule, ICollectionSnapshotValidator
{
    public string ModuleKey => ClassicPageModule.Key;
    public string InputVersion => ClassicPageModule.InputVersion;
    public async IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var journal = new ClassicPageAcquisitionJournal(context);
        await journal.InitializeAsync(cancellationToken);
        await using var source = online == null ? new ClassicPageOnlineSource(context, await context.Environment.OpenAsync(context.Options, cancellationToken))
            : await online(context, cancellationToken);
        var plan = await journal.ReadOrAcquireAsync("ScopePlan", "assessment", () => source.ReadSitesAsync(ClassicPageModule.Options(context.Options), cancellationToken), cancellationToken);
        var sites = new List<ClassicPageSiteScope>();
        var webs = new List<(string Site, string Web, string Template)>();
        foreach (var site in plan.Sites)
        {
            var scope = await journal.ReadOrAcquireAsync("SiteScope", site.ToLowerInvariant(), () => source.ReadWebsAsync(site, cancellationToken), cancellationToken,
                x => ClassicPageAcquisitionJournal.Status(x.State), x => x.State.Error);
            if (scope.WebUrls.Length != scope.Templates.Length) throw new SnapshotIntegrityException("Web scope identities and templates are inconsistent.");
            sites.Add(scope);
            webs.AddRange(scope.WebUrls.Select((web, index) => (site, web, scope.Templates[index])));
        }
        var fixedScope = plan with { SiteScopes = sites.ToArray() };
        await journal.ReadOrAcquireAsync("Scope", "assessment", () => Task.FromResult(fixedScope), cancellationToken);
        var workers = new ActionBlock<(string Site, string Web, string Template)>(async work =>
        {
            var key = ClassicPageSourceJson.WebKey(work.Site, work.Web);
            var identity = await journal.ReadOrAcquireAsync("WebIdentity", key, async () =>
            {
                try { return await source.ReadIdentityAsync(work.Site, work.Web, work.Template, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { return new ClassicPageWebSource(work.Site, work.Web, work.Template, Guid.Empty, Guid.Empty, default, [], [], null,
                    SourceReadState.NotAttempted, null, SourceReadState.NotAttempted, null, 0, null, SourceReadState.NotAttempted, ClassicPageOnlineSource.Failure(ex)); }
            }, cancellationToken, x => ClassicPageAcquisitionJournal.Status(x.State), x => x.State.Error);
            var discovery = identity.State.Succeeded
                ? await journal.ReadOrAcquireAsync("Discovery", key, () => source.DiscoverAsync(identity, journal, cancellationToken), cancellationToken)
                : new ClassicPageDiscoverySource(work.Site, work.Web, []);
            var web = await journal.ReadOrAcquireAsync("Web", key, async () =>
            {
                if (!identity.State.Succeeded) return identity;
                try { return await source.ReadWebAsync(work.Site, work.Web, work.Template, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { return new ClassicPageWebSource(work.Site, work.Web, work.Template, identity.SiteId, identity.WebId, default, [], [], null,
                    SourceReadState.NotAttempted, null, SourceReadState.NotAttempted, null, 0, null, SourceReadState.NotAttempted, ClassicPageOnlineSource.Failure(ex)); }
            }, cancellationToken, x => ClassicPageAcquisitionJournal.Status(x.State), x => x.State.Error);
            foreach (var page in discovery.Rows.Where(x => x.RowType == "Page"))
            {
                await journal.ReadOrAcquireAsync("Page", key + "|" + page.RecordKey, async () =>
                {
                    if (!web.State.Succeeded) return ClassicPageOnlineSource.EmptyPage(web, page, web.State);
                    try { return await source.ReadPageAsync(web, page, plan.Options.SkipUserInformation, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { return ClassicPageOnlineSource.EmptyPage(web, page, ClassicPageOnlineSource.Failure(ex)); }
                }, cancellationToken, x => x.MetadataState.Succeeded
                    ? x.WebPartsState.Status is "Failed" or "Denied" ? AcquisitionStatus.Partial : AcquisitionStatus.Complete
                    : ClassicPageAcquisitionJournal.Status(x.MetadataState), x => x.MetadataState.Error ?? x.WebPartsState.Error);
            }
            if (web.State.Succeeded && web.Template == "BLOG#0")
            {
                string? paging = null; var ordinal = 0; var seen = new HashSet<string>();
                do
                {
                    var batch = await journal.ReadOrAcquireAsync("Blog", key + "|" + ordinal++, async () =>
                        await source.ReadBlogBatchAsync(web, paging, plan.Options.SkipUserInformation, cancellationToken)
                        ?? new ClassicPageBlogBatchSource(web.SiteUrl, web.WebUrl, Guid.Empty, "", "", [], null, SourceReadState.NotAttempted), cancellationToken);
                    paging = batch.NextPage;
                    if (paging != null && !seen.Add(paging)) throw new SnapshotIntegrityException("Blog pagination repeated a continuation.");
                } while (paging != null);
            }
        }, new ExecutionDataflowBlockOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = context.Options.Threads,
            BoundedCapacity = Math.Max(1, context.Options.Threads * 2) });
        try { foreach (var web in webs) if (!await workers.SendAsync(web, cancellationToken)) break; }
        finally { workers.Complete(); await workers.Completion; }
        if (!plan.Options.SkipUsageInformation) await source.CollectAuditAsync(fixedScope, journal, cancellationToken);
        yield return new(null, VersionedJson.From(new { stage = "ClassicPageCollectionComplete" }));
    }
    public async Task ValidateAsync(ICollectionJournal journal, CancellationToken cancellationToken)
    {
        var headers = await journal.ReadCommittedAsync(cancellationToken, metadataOnly: true);
        async IAsyncEnumerable<SourceRecord> Records()
        {
            foreach (var header in headers)
            {
                if (ClassicPageSourceJson.Kind(header) is not ("Scope" or "Web" or "Discovery" or "Page" or "AuditPage" or "AuditChunk")) continue;
                var record = await journal.ReadAsync(header.ObservationId, cancellationToken);
                yield return new(record.ObservationId, Guid.Empty, record.SourceIdentity, record.SourceRevision, record.AcquisitionStatus,
                    record.Metadata, new SourceArtifact(record.RawBytes?.LongLength, null, record.RawBytes), record.AcquisitionError);
            }
        }
        await ClassicPageInputIndex.CreateAsync(Records(), cancellationToken);
    }
}
