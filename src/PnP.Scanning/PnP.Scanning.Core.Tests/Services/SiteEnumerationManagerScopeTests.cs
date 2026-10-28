using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PnP.Core.Auth;
using PnP.Core.Services;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Services;

public class SiteEnumerationManagerScopeTests
{
    private const string TeamSite = "https://contoso.sharepoint.com/sites/team";
    private const string PersonalSite = "https://contoso-my.sharepoint.com/personal/alex_contoso_com";

    /// <summary>
    /// Defers M1 tenant-site admission until templates can be evaluated while retaining the legacy non-page personal-host filter.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tenant_authority_defers_M1_admission_to_template_policy_and_keeps_legacy_host_filter(bool pages, bool homePageOnly)
    {
        using var fixture = new OfflineEnumeration("STS#0", TeamSite);
        var start = new StartRequest
        {
            Mode = Mode.Classic.ToString(), Tenant = "contoso.sharepoint.com",
            MySiteHostUrl = "https://contoso-my.sharepoint.com",
            AdminCenterUrl = "https://contoso-admin.sharepoint.com",
        };
        ClassicStartRequestBuilder.AddClassicProperties(start,
            pages ? new[] { ClassicComponent.Pages, ClassicComponent.Lists } : new[] { ClassicComponent.Lists },
            false, true, true, homePageOnly);

        var sites = await fixture.Manager.EnumerateSiteCollectionsToScanAsync(start, null, _ => { });

        if (pages) sites.Should().Equal(new[] { TeamSite, PersonalSite }, "M1 must observe templates before admission; none can clear the defaults");
        else sites.Should().Equal(new[] { TeamSite }, "legacy non-page scans retain their host filter");
        fixture.Transport.Requests.Count(url => url.Contains("sites/getallsites", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "the real SDK tenant enumeration must run against the offline response");
        fixture.Transport.Unexpected.Should().BeEmpty();
    }

    /// <summary>
    /// Excludes personal roots by template yet lets M1 inspect child templates independently; legacy enumeration still skips the root subtree.
    /// </summary>
    [Theory]
    [InlineData("SPSPERS#0", false, false)]
    [InlineData("SPSPERS#0", true, false)]
    [InlineData("SPSPERS#0", true, true)]
    [InlineData("SPSPERS#10", false, false)]
    [InlineData("SPSPERS#10", true, false)]
    [InlineData("SPSPERS#10", true, true)]
    public async Task Explicit_personal_root_is_excluded_but_M1_inspects_independent_child_templates(string template, bool pages, bool homePageOnly)
    {
        using var fixture = new OfflineEnumeration(template, PersonalSite);

        var result = await fixture.Manager.EnumerateWebsToScanAsync(Guid.NewGuid(), PersonalSite,
            Options(pages, homePageOnly), null, isRestart: false);

        if (pages)
        {
            result.Webs.Select(web => (web.WebUrl, web.WebTemplate)).Should().Equal(("/ordinary", "STS#0"));
            result.ScopeEvidence.Should().Contain(row => row.WebUrl == "/" && row.DiscoveryStatus == "PolicyExcluded");
            result.ScopeEvidence.Should().Contain(row => row.WebUrl == "/ordinary" && row.DiscoveryStatus == "Complete");
            fixture.Transport.Requests.Count(url => url.Contains("getsubwebsfilteredforcurrentuser")).Should().Be(3);
        }
        else
        {
            result.Webs.Should().BeEmpty();
            fixture.Transport.Requests.Should().NotContain(url => url.Contains("getsubwebsfilteredforcurrentuser"));
        }
        fixture.Transport.Unexpected.Should().BeEmpty();
    }

    /// <summary>
    /// Excludes app webs without losing regular webs and distinguishes M1 child-template inspection from legacy subtree skipping.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task App_webs_are_skipped_without_losing_regular_webs(bool pages, bool homePageOnly)
    {
        using var fixture = new OfflineEnumeration("STS#0", TeamSite);

        var result = await fixture.Manager.EnumerateWebsToScanAsync(Guid.NewGuid(), TeamSite,
            Options(pages, homePageOnly), null, isRestart: false);

        result.Webs.Select(web => (web.WebUrl, web.WebTemplate)).Should().BeEquivalentTo(new[]
        {
            ("/", "STS#0"), ("/ordinary", "STS#0"),
        });
        fixture.Transport.Requests.Count(url => url.Contains("getsubwebsfilteredforcurrentuser"))
            .Should().Be(pages ? 3 : 2, "M1 observes child templates before filtering, while legacy scans retain APP subtree skipping");
        if (pages) result.ScopeEvidence.Should().Contain(row => row.WebUrl == "/app" && row.DiscoveryStatus == "PolicyExcluded");
        fixture.Transport.Unexpected.Should().BeEmpty();
    }

    /// <summary>
    /// Applies an explicit none policy in the real web enumerator so personal and app templates remain admitted with complete scope evidence.
    /// </summary>
    [Fact]
    public async Task Sole_none_clears_defaults_in_the_real_web_enumerator()
    {
        using var fixture = new OfflineEnumeration("SPSPERS#0", PersonalSite);
        var options = Options(true, false); options.ExcludeSiteTemplates = "none";
        var result = await fixture.Manager.EnumerateWebsToScanAsync(Guid.NewGuid(), PersonalSite, options, null, false);
        result.Webs.Select(web => web.WebUrl).Should().BeEquivalentTo("/", "/ordinary", "/app");
        result.ScopeEvidence.Should().HaveCount(3).And.OnlyContain(row => row.DiscoveryStatus == "Complete");
        fixture.Transport.Unexpected.Should().BeEmpty();
    }

    /// <summary>
    /// Replaces built-in exclusions with explicit patterns so only the configured template is filtered from the observed web set.
    /// </summary>
    [Fact]
    public async Task Explicit_patterns_replace_defaults_instead_of_extending_them()
    {
        using var fixture = new OfflineEnumeration("SPSPERS#0", PersonalSite);
        var options = Options(true, false); options.ExcludeSiteTemplates = "STS#0";
        var result = await fixture.Manager.EnumerateWebsToScanAsync(Guid.NewGuid(), PersonalSite, options, null, false);
        result.Webs.Select(web => web.WebUrl).Should().BeEquivalentTo("/", "/app");
        result.ScopeEvidence.Should().Contain(row => row.WebUrl == "/ordinary" && row.DiscoveryStatus == "PolicyExcluded");
        result.ScopeEvidence.Should().Contain(row => row.WebUrl == "/" && row.DiscoveryStatus == "Complete");
        fixture.Transport.Unexpected.Should().BeEmpty();
    }

    /// <summary>
    /// Continues beneath an excluded app parent and admits a publishing child whose own template is allowed.
    /// </summary>
    [Fact]
    public async Task An_excluded_app_parent_does_not_prune_an_allowed_publishing_child()
    {
        using var fixture = new OfflineEnumeration("STS#0", TeamSite, allowedChildUnderApp: true);
        var options = Options(true, false); options.ExcludeSiteTemplates = "STS#0,APP#*";
        var result = await fixture.Manager.EnumerateWebsToScanAsync(Guid.NewGuid(), TeamSite, options, null, false);
        result.Webs.Select(web => (web.WebUrl, web.WebTemplate)).Should().Equal(("/app/allowed", "CMSPUBLISHING#0"));
        result.ScopeEvidence.Should().Contain(row => row.WebUrl == "/app" && row.DiscoveryStatus == "PolicyExcluded");
        result.ScopeEvidence.Should().Contain(row => row.WebUrl == "/app/allowed" && row.DiscoveryStatus == "Complete");
        fixture.Transport.Unexpected.Should().BeEmpty();
    }

    private static ClassicOptions Options(bool pages, bool homePageOnly) => new()
    {
        Mode = Mode.Classic.ToString(), Pages = pages, HomePageOnly = homePageOnly, Lists = true,
    };

    private sealed class OfflineEnumeration : IDisposable
    {
        private readonly ServiceProvider services;
        internal readonly EnumerationTransport Transport;
        internal readonly SiteEnumerationManager Manager;

        /// <summary>
        /// Wires the real SDK and web enumerator to an offline transport with configurable root template and optional allowed app descendant.
        /// </summary>
        internal OfflineEnumeration(string template, string siteUrl, bool allowedChildUnderApp = false)
        {
            Transport = new EnumerationTransport(template, siteUrl, allowedChildUnderApp);
            var collection = new ServiceCollection();
            collection.AddPnPCore(options => options.DisableTelemetry = true,
                configureSharePointRest: builder => builder.ConfigurePrimaryHttpMessageHandler(() => Transport),
                configureMicrosoftGraph: builder => builder.ConfigurePrimaryHttpMessageHandler(() => Transport));
            services = collection.BuildServiceProvider();
            var factory = DispatchProxy.Create<IPnPContextFactory, OfflineContextFactory>();
            var proxy = (OfflineContextFactory)(object)factory;
            proxy.Inner = services.GetRequiredService<IPnPContextFactory>();
            Manager = new SiteEnumerationManager(null, factory);
        }

        public void Dispose() => services.Dispose();
    }

    // Only replaces authentication. The real SDK still initializes contexts and enumerates sites/webs.
    public class OfflineContextFactory : DispatchProxy
    {
        internal IPnPContextFactory Inner { get; set; }
        private static readonly string Token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: new[] { new Claim("roles", "Sites.Read.All") }));

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IPnPContextFactory.CreateAsync) && args.Length == 3 && args[0] is Uri uri)
                return Inner.CreateAsync(uri, new ExternalAuthenticationProvider((_, _) => Task.FromResult(Token)),
                    (PnPContextOptions)args[2]);
            throw new InvalidOperationException("Unexpected context factory operation: " + method.Name);
        }
    }

    /// <summary>
    /// Serves deterministic tenant and web metadata for one root, optionally exposing an allowed child beneath an excluded app web.
    /// </summary>
    private sealed class EnumerationTransport(string template, string siteUrl, bool allowedChildUnderApp) : HttpMessageHandler
    {
        private const string SiteId = "11111111-1111-1111-1111-111111111111";
        private const string WebId = "22222222-2222-2222-2222-222222222222";
        internal readonly List<string> Requests = new();
        internal readonly List<string> Unexpected = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) throw UnexpectedRequest(request.Method, request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Payload(request.RequestUri), Encoding.UTF8, "application/json"),
            });
        }

        /// <summary>
        /// Records and serves only modeled tenant, identity, and child-web requests, including an independently allowed descendant under an app web.
        /// </summary>
        private string Payload(Uri uri)
        {
            Requests.Add(uri.AbsoluteUri);
            var path = uri.AbsolutePath;
            if (uri.Host == "graph.microsoft.com" && path.EndsWith("/sites/getallsites", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { value = new[] { GraphSite(TeamSite, SiteId),
                    GraphSite(PersonalSite, "33333333-3333-3333-3333-333333333333") } });
            if (uri.Host is not ("contoso.sharepoint.com" or "contoso-my.sharepoint.com"))
                throw UnexpectedRequest(HttpMethod.Get, uri);
            if (path.EndsWith("/_api/Web", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { Id = WebId, Url = uri.GetLeftPart(UriPartial.Authority) +
                    path[..path.IndexOf("/_api/", StringComparison.OrdinalIgnoreCase)],
                    WebTemplateConfiguration = template, RegionalSettings = new { TimeZone = new { Id = 2 } } });
            if (path.EndsWith("/_api/Site", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { Id = SiteId, GroupId = Guid.Empty });
            if (uri.AbsoluteUri.Contains("getsubwebsfilteredforcurrentuser", StringComparison.OrdinalIgnoreCase))
            {
                var webUrl = uri.GetLeftPart(UriPartial.Authority) + path[..path.IndexOf("/_api/", StringComparison.OrdinalIgnoreCase)];
                if (webUrl == siteUrl)
                    return JsonSerializer.Serialize(new { value = new[] { Subweb("ordinary", "STS"), Subweb("app", "APP") } });
                if (webUrl == siteUrl + "/app" && allowedChildUnderApp)
                    return JsonSerializer.Serialize(new { value = new[] { Subweb("app/allowed", "CMSPUBLISHING") } });
                if (webUrl == siteUrl + "/ordinary" || webUrl == siteUrl + "/app" || webUrl == siteUrl + "/app/allowed") return "{\"value\":[]}";
            }
            throw UnexpectedRequest(HttpMethod.Get, uri);
        }

        private static object GraphSite(string url, string id) => new
        {
            id = new Uri(url).Host + "," + id + "," + WebId, webUrl = url, displayName = "Offline site",
            sharepointIds = new { siteId = id, webId = WebId }, root = new { },
        };

        /// <summary>
        /// Builds deterministic child-web metadata with distinct app and descendant identities and the requested template.
        /// </summary>
        private object Subweb(string name, string webTemplate) => new
        {
            Id = name == "app" ? "44444444-4444-4444-4444-444444444444" : name == "app/allowed" ? "66666666-6666-6666-6666-666666666666" : "55555555-5555-5555-5555-555555555555",
            ServerRelativeUrl = new Uri(siteUrl).AbsolutePath + "/" + name,
            WebTemplate = webTemplate, WebTemplateId = 0, Language = 1033,
            Created = "2020-01-01T00:00:00Z", LastItemModifiedDate = "2020-01-01T00:00:00Z",
            LastItemUserModifiedDate = "2020-01-01T00:00:00Z",
        };

        private Exception UnexpectedRequest(HttpMethod method, Uri uri)
        {
            var message = method + " " + uri;
            Unexpected.Add(message);
            return new InvalidOperationException("Offline fixture rejected unexpected request: " + message);
        }
    }
}
