using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class AspxDiscoveryOrchestrationTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public async Task Full_hierarchy_persists_denominator_and_lossless_raw_inventory()
    {
        using var directory = new TemporaryDirectory();
        var input = CompleteInput();
        using var provider = await LoadProvider(directory, input);

        var output = await new AspxInventoryRuntime().RunAsync(provider,
            new(directory.DatabasePath, directory.OutputPath, Manifest(), "tenant_full", FixtureRun: true));

        output.OutputVersion.Should().Be(AspxDiscoveryOutputV2.Version);
        output.CoverageVerdict.Should().Be(DiscoveryVerdict.CompleteAuthorizedSurface);
        output.Coverage.Select(row => row.Kind).Should().Contain(new[]
        {
            nameof(DiscoveryScopeKind.Tenant), nameof(DiscoveryScopeKind.Geo),
            nameof(DiscoveryScopeKind.SiteCollection), nameof(DiscoveryScopeKind.Web),
            nameof(DiscoveryScopeKind.Container), nameof(DiscoveryScopeKind.Folder),
        });
        output.Denominator.Should().HaveCount(8);
        output.Denominator.Should().OnlyContain(row => row.ExpectedCount == row.ObservedCount);
        output.Inventory.Select(row => row.FileName).Should().BeEquivalentTo(
            "Upper.ASPX", "hidden-custom.aspx", "classification-failed.aspx");
        output.Inventory.Should().NotContain(row => row.FileName.EndsWith(".aspx.bak", StringComparison.OrdinalIgnoreCase));
        output.UnresolvedGapCodes.Should().BeEmpty();
        File.Exists(directory.DatabasePath).Should().BeTrue();
        File.Exists(directory.OutputPath).Should().BeTrue();
    }

    [Fact]
    public async Task Single_container_without_complete_denominator_fails_closed()
    {
        using var directory = new TemporaryDirectory();
        var input = CompleteInput() with
        {
            Scopes = CompleteInput().Scopes.Where(scope => scope.ScopeKey is not ("container-2" or "folder-2"))
                .Select(scope => scope.ScopeKey == "web"
                    ? scope with { ExpectedChildScopeKeys = new[] { "container-1" } }
                    : scope).ToArray(),
        };
        using var provider = await LoadProvider(directory, input);

        var output = await new AspxInventoryRuntime().RunAsync(provider,
            new(directory.DatabasePath, directory.OutputPath, Manifest(), "tenant_full", FixtureRun: true));

        output.CoverageVerdict.Should().Be(DiscoveryVerdict.Incomplete);
        output.UnresolvedGapCodes.Should().Contain(DiscoveryGapCodes.ExpectedChildMissing);
    }

    [Fact]
    public async Task Missing_expected_child_is_a_real_persisted_gap()
    {
        using var directory = new TemporaryDirectory();
        var input = CompleteInput() with
        {
            Scopes = CompleteInput().Scopes.Select(scope => scope.ScopeKey == "web"
                ? scope with { ExpectedChildScopeKeys = new[] { "container-1", "container-2", "container-missing" } }
                : scope).ToArray(),
        };
        using var provider = await LoadProvider(directory, input);

        var output = await new AspxInventoryRuntime().RunAsync(provider,
            new(directory.DatabasePath, directory.OutputPath, Manifest(), "tenant_full", FixtureRun: true));

        output.CoverageVerdict.Should().Be(DiscoveryVerdict.Incomplete);
        output.UnresolvedGapCodes.Should().Contain(DiscoveryGapCodes.ExpectedChildMissing);
        output.Denominator.Should().ContainSingle(row => row.ParentScopeKey == "web" &&
            row.ExpectedCount == 3 && row.ObservedCount == 2);
    }

    [Theory]
    [InlineData(DiscoveryTerminalOutcome.Denied, null, DiscoveryVerdict.Incomplete)]
    [InlineData(DiscoveryTerminalOutcome.Truncated, null, DiscoveryVerdict.Incomplete)]
    [InlineData(DiscoveryTerminalOutcome.Unknown, DiscoveryGapCodes.SourceUnsupported, DiscoveryVerdict.Unknown)]
    [InlineData(DiscoveryTerminalOutcome.Unknown, DiscoveryGapCodes.PermissionVisibilityUnknown, DiscoveryVerdict.Unknown)]
    public async Task Permission_unsupported_and_truncation_states_cannot_complete(
        DiscoveryTerminalOutcome outcome, string gapCode, DiscoveryVerdict expected)
    {
        using var directory = new TemporaryDirectory();
        var input = CompleteInput() with
        {
            Scopes = CompleteInput().Scopes.Select(scope => scope.ScopeKey == "folder-1"
                ? scope with
                {
                    Batches = new[]
                    {
                        Batch(0, new[] { Record("partial", "partial.aspx") }, true, outcome,
                            gapCode: gapCode, gapDetail: "synthetic adverse terminal state"),
                    },
                }
                : scope).ToArray(),
        };
        using var provider = await LoadProvider(directory, input);

        var output = await new AspxInventoryRuntime().RunAsync(provider,
            new(directory.DatabasePath, directory.OutputPath, Manifest(), "tenant_full", FixtureRun: true));

        output.CoverageVerdict.Should().Be(expected);
        if (gapCode != null) output.UnresolvedGapCodes.Should().Contain(gapCode);
        output.Inventory.Should().Contain(row => row.FileName == "partial.aspx");
    }

    [Fact]
    public async Task Pagination_resume_and_denominator_drift_are_auditable_without_count_multiplication()
    {
        using var directory = new TemporaryDirectory();
        var input = CompleteInput() with
        {
            Scopes = CompleteInput().Scopes.Select(scope => scope.ScopeKey == "folder-1"
                ? scope with
                {
                    Batches = new[]
                    {
                        Batch(0, new[] { Record("p1", "page-1.aspx") }, false,
                            DiscoveryTerminalOutcome.Pending, "token-1"),
                        Batch(1, new[] { Record("p2", "page-2.ASPX") }, true,
                            DiscoveryTerminalOutcome.Complete),
                    },
                }
                : scope).ToArray(),
        };
        var runtime = new AspxInventoryRuntime();
        using var firstProvider = await LoadProvider(directory, input);
        var first = await runtime.RunAsync(firstProvider,
            new(directory.DatabasePath, directory.OutputPath, Manifest(), "tenant_full", FixtureRun: true));

        using var resumedProvider = await LoadProvider(directory, input);
        var resumed = await runtime.RunAsync(resumedProvider,
            new(directory.DatabasePath, directory.OutputPath, Manifest(), "tenant_full", FixtureRun: true,
                ResumeRunId: first.RunId));

        resumed.Inventory.Select(row => row.FileName).Should().Contain(new[] { "page-1.aspx", "page-2.ASPX" });
        resumed.Coverage.Single(row => row.ScopeKey == "folder-1").Counts
            .Should().Match<DiscoveryCounts>(counts => counts.AttemptCount == 2 && counts.BatchCount == 2 &&
                counts.ObservedCount == 2 && counts.EmittedCount == 2);
        resumed.UnresolvedGapCodes.Should().NotContain(DiscoveryGapCodes.DenominatorDrift);

        var driftedInput = input with
        {
            Scopes = input.Scopes.Select(scope => scope.ScopeKey == "web"
                ? scope with { ExpectedChildScopeKeys = new[] { "container-1", "container-2", "container-new" } }
                : scope).ToArray(),
        };
        using var driftedProvider = await LoadProvider(directory, driftedInput);
        var drifted = await runtime.RunAsync(driftedProvider,
            new(directory.DatabasePath, directory.OutputPath, Manifest(), "tenant_full", FixtureRun: true,
                ResumeRunId: first.RunId));
        drifted.CoverageVerdict.Should().Be(DiscoveryVerdict.Unknown);
        drifted.UnresolvedGapCodes.Should().Contain(DiscoveryGapCodes.DenominatorDrift);
    }

    [Fact]
    public async Task Resume_rejects_immutable_provenance_drift_before_enumeration()
    {
        using var directory = new TemporaryDirectory();
        using var provider = await LoadProvider(directory, CompleteInput());
        var runtime = new AspxInventoryRuntime();
        var first = await runtime.RunAsync(provider,
            new(directory.DatabasePath, directory.OutputPath, Manifest(), "tenant_full", FixtureRun: true));
        using var resumedProvider = await LoadProvider(directory, CompleteInput());

        var action = () => runtime.RunAsync(resumedProvider,
            new(directory.DatabasePath, directory.OutputPath,
                Manifest() with { EnvironmentManifestHash = new string('b', 64) },
                "tenant_full", FixtureRun: true, ResumeRunId: first.RunId));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Immutable discovery provenance drift*EnvironmentManifestHash*");
    }

    internal static AspxDiscoveryInputV1 CompleteInput()
    {
        var scopes = new[]
        {
            Scope("tenant", null, DiscoveryScopeKind.Tenant, new[] { "geo" }),
            Scope("geo", "tenant", DiscoveryScopeKind.Geo, new[] { "site" }),
            Scope("site", "geo", DiscoveryScopeKind.SiteCollection, new[] { "web" }),
            Scope("web", "site", DiscoveryScopeKind.Web, new[] { "container-1", "container-2" }),
            Scope("container-1", "web", DiscoveryScopeKind.Container, new[] { "folder-1" }),
            Scope("container-2", "web", DiscoveryScopeKind.Container, new[] { "folder-2" }),
            Scope("folder-1", "container-1", DiscoveryScopeKind.Folder, Array.Empty<string>(),
                DiscoverySourceKind.RawListLibraryFiles, new[]
                {
                    Batch(0, new[]
                    {
                        Record("upper", "Upper.ASPX"),
                        Record("hidden", "hidden-custom.aspx", ("hidden", "true")),
                        Record("backup", "not-a-page.aspx.bak"),
                    }, true, DiscoveryTerminalOutcome.Complete),
                }),
            Scope("folder-2", "container-2", DiscoveryScopeKind.Folder, Array.Empty<string>(),
                DiscoverySourceKind.RawListLibraryFiles, new[]
                {
                    Batch(0, new[]
                    {
                        Record("classified", "classification-failed.aspx", ("classificationError", "synthetic")),
                    }, true, DiscoveryTerminalOutcome.Complete),
                }),
        };
        return new(AspxDiscoveryInputV1.CurrentVersion, "tenant", "tenant_full", FixtureRun: true,
            TenantVisibilityVerified: false, scopes);
    }

    internal static DiscoveryRunManifest Manifest() => new(
        "pnp/pnpassessment@34f34e2e3c56909ed5fc98afcb118390b025d7db",
        "1f07296b186698c3cc9ca8580f00af36c0f3f4f5",
        DiscoveryRunManifest.CurrentContractVersion, DiscoveryRunManifest.CurrentSchemaVersion,
        Hash, Hash, Hash, Hash, Hash, Hash, Hash, Hash, "synthetic-fixtures/ccd-70", Hash);

    internal static async Task<ManifestAspxDiscoveryProvider> LoadProvider(TemporaryDirectory directory,
        AspxDiscoveryInputV1 input)
    {
        var path = directory.NextInputPath();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(input, AspxInventoryRuntime.JsonOptions(indented: true)));
        return await ManifestAspxDiscoveryProvider.LoadAsync(path);
    }

    private static AspxDiscoveryInputScope Scope(string key, string parent, DiscoveryScopeKind kind,
        IReadOnlyList<string> expected, DiscoverySourceKind? source = null,
        IReadOnlyList<RawDiscoveryBatch> batches = null) => new(
            key, parent, kind, source, "/" + key, "synthetic-authorized", Required: true,
            expected.Count == 0 ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete,
            expected, batches ?? Array.Empty<RawDiscoveryBatch>());

    private static RawDiscoveryRecord Record(string id, string fileName,
        params (string Key, string Value)[] metadata) => new(
            id, id, "container", fileName, "/raw/" + fileName, true, "synthetic-authorized",
            metadata.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));

    private static RawDiscoveryBatch Batch(int ordinal, IReadOnlyList<RawDiscoveryRecord> records, bool terminal,
        DiscoveryTerminalOutcome outcome, string next = null, string gapCode = null, string gapDetail = null) => new(
            ordinal, DiscoveryHash.Of("request", ordinal.ToString()),
            DiscoveryHash.Of("response", ordinal.ToString(), string.Join(",", records.Select(record => record.FileName))),
            records, terminal, outcome, next, gapCode, gapDetail);

    internal sealed class TemporaryDirectory : IDisposable
    {
        private int inputOrdinal;

        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aspx-discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }
        internal string DatabasePath => System.IO.Path.Combine(Path, "inventory.sqlite");
        internal string OutputPath => System.IO.Path.Combine(Path, "aspx-discovery-output-v2.json");
        internal string NextInputPath() => System.IO.Path.Combine(Path, $"input-{++inputOrdinal}.json");
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
