using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Net;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class AspxAcquisitionV3Tests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private static readonly Guid RunId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void P1_P5_pagination_integrity_and_denied_terminal_fail_closed()
    {
        var p1 = AspxPaginationContract.Validate(new[]
        {
            Page(0, null, "token-1", terminal: false),
            Page(2, "token-1", null, terminal: true),
        }, DiscoveryTerminalOutcome.Complete);
        var p2 = AspxPaginationContract.Validate(new[]
        {
            Page(0, null, "token-1", terminal: false),
            Page(0, "token-1", null, terminal: true),
        }, DiscoveryTerminalOutcome.Complete);
        var p3 = AspxPaginationContract.Validate(new[] { Page(0, null, "token-1", terminal: true) },
            DiscoveryTerminalOutcome.Complete);
        var p4 = AspxPaginationContract.Validate(new[] { Page(0, null, "token-1", terminal: false) },
            DiscoveryTerminalOutcome.Complete);
        var p5 = AspxPaginationContract.Validate(new[] { Page(0, null, null, terminal: true) },
            DiscoveryTerminalOutcome.Denied);

        new[] { p1, p2, p3, p4 }.Should().OnlyContain(result => result.Outcome == DiscoveryTerminalOutcome.Unknown);
        p4.OutstandingTokenCount.Should().Be(1);
        p5.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied,
            "a denied child with a null next token is not complete");
    }

    [Fact]
    public void R1_R5_registry_build_drift_alias_and_runtime_counterexamples_are_unknown()
    {
        Registry(min: "16.0.1", max: "16.0.2").Validate("unknown-build").Should().NotBeEmpty(); // R1

        var physical = Physical();
        var manifest = ReferenceManifest() with { RegistryHash = HashB };
        var reference = Reference(Array.Empty<AspxSurfaceDenominatorRow>());
        AspxAggregateEvaluator.Evaluate(physical, reference, manifest, Registry(), out var r2Gaps)
            .Should().Be(AspxAggregateVerdict.Unknown); // R2
        r2Gaps.Should().Contain("registry:hash_drift");

        var collision = Registry(entries: new[]
        {
            Entry("one", "/_layouts/15/a.aspx", new[] { "/_layouts/15/shared.aspx" }),
            Entry("two", "/_layouts/15/b.aspx", new[] { "/_layouts/15/shared.aspx" }),
        });
        collision.Validate("16.0.1").Should().Contain(item => item.StartsWith("registry_alias_collision")); // R3

        var runtimeCounterexample = reference with { GapCodes = new[] { "runtime_virtual_path_absent_from_registry" } };
        AspxAggregateEvaluator.Evaluate(physical, runtimeCounterexample, ReferenceManifest(), Registry(), out _)
            .Should().Be(AspxAggregateVerdict.Unknown); // R4

        AspxAggregateEvaluator.Evaluate(physical, reference, ReferenceManifest(),
            Registry(min: "17.0.0", max: "17.0.1"), out _).Should().Be(AspxAggregateVerdict.Unknown); // R5
    }

    [Theory]
    [InlineData(AspxReferenceDispositions.ReferenceOnlyAvailable)]
    [InlineData(AspxReferenceDispositions.ReferenceUnavailable)]
    [InlineData(AspxReferenceDispositions.VirtualHandler)]
    public void I1_I3_reference_only_states_reject_fake_physical_identity(string disposition)
    {
        Observation(disposition, HashA, "file-id").Validate()
            .Should().Contain("non_physical_disposition_has_physical_identity");
    }

    [Fact]
    public void I4_I5_many_references_share_one_physical_row_and_ambiguous_join_is_unknown()
    {
        var fileId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var key = DiscoveryHash.Of("file", fileId);
        var physical = Physical(new DiscoveryInventoryRow("scope", key, "/forms/shared.aspx",
            "shared.aspx", "strong", "fixture"));
        var collector = new AspxReferenceCollector();
        collector.AddReference(Candidate("form-1", "/forms/shared.aspx", fileId));
        collector.AddReference(Candidate("view-1", "/forms/shared.aspx", fileId));
        var output = collector.Build(RunId, ReferenceManifest(), physical, Registry());

        output.References.Should().HaveCount(2);
        output.References.Should().OnlyContain(item => item.LinkedPhysicalCanonicalInventoryKey == key);
        physical.Inventory.Should().ContainSingle("physical identity is not multiplied by reference edges"); // I4

        var missing = new AspxReferenceCollector();
        missing.AddReference(Candidate("missing", "/forms/missing.aspx", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var failed = missing.Build(RunId, ReferenceManifest(), physical, Registry());
        failed.CoverageVerdict.Should().Be(AspxAggregateVerdict.Unknown);
        failed.References.Single().Disposition.Should().Be(AspxReferenceDispositions.Unknown); // I5
    }

    [Fact]
    public void V1_V7_exact_version_and_companion_binding_reject_legacy_or_drifted_composition()
    {
        AspxAcquisitionEnvelopeValidator.PhysicalOnly(AspxDiscoveryOutputV2.Version)
            .Should().Match<AspxAcquisitionEnvelopeValidation>(value => value.PhysicalOnly &&
                value.Verdict == AspxAggregateVerdict.Unknown); // V1
        AspxAcquisitionEnvelopeValidator.Validate(null, HashA, HashB,
            AspxDiscoveryOutputV2.Version, AspxReferenceOutputV1.Version).GapCodes
            .Should().Contain("acquisition_envelope_missing"); // V2

        var envelope = Envelope();
        AspxAcquisitionEnvelopeValidator.Validate(envelope, HashB, HashB,
            AspxDiscoveryOutputV2.Version, AspxReferenceOutputV1.Version).GapCodes
            .Should().Contain("physical_hash_mismatch"); // V3
        Observation("future-disposition", null, null).Validate().Should().Contain("unknown_disposition"); // V4

        var mixedFence = envelope with
        {
            ReferenceVolume = envelope.ReferenceVolume with { SnapshotFence = "other-fence" },
        };
        AspxAcquisitionEnvelopeValidator.Validate(mixedFence, HashA, HashB,
            AspxDiscoveryOutputV2.Version, AspxReferenceOutputV1.Version).GapCodes
            .Should().Contain("snapshot_fence_mismatch"); // V5

        var mixedProduct = envelope with
        {
            ReferenceVolume = envelope.ReferenceVolume with
            {
                ProductRef = "pnp/assessment@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            },
        };
        AspxAcquisitionEnvelopeValidator.Validate(mixedProduct, HashA, HashB,
            AspxDiscoveryOutputV2.Version, AspxReferenceOutputV1.Version).GapCodes
            .Should().Contain("product_ref_mismatch"); // V6
        AspxAcquisitionEnvelopeValidator.Validate(envelope, HashA, HashB,
            AspxDiscoveryOutputV2.Version, "aspx-discovery-output/v2").GapCodes
            .Should().Contain("reference_version_incompatible"); // V7
    }

    [Fact]
    public async Task F1_F8_and_A1_A2_actual_BaseType_controls_raw_admission_while_all_lists_keep_forms_views()
    {
        foreach (var baseType in new[] { 0, 3, 4, 5 })
        {
            var decision = AspxListApplicabilityPolicy.Evaluate(baseType);
            decision.RawLibraryRequired.Should().BeFalse();
            decision.FormsAndViewsRequired.Should().BeTrue();
            decision.Applicability.Should().Be(AspxSurfaceApplicability.SystemOrVirtualOnly);
        }
        AspxListApplicabilityPolicy.Evaluate(null).Outcome.Should().Be(DiscoveryTerminalOutcome.Unknown); // A1
        AspxListApplicabilityPolicy.Evaluate(1).RawLibraryRequired.Should().BeTrue(); // A2, template is irrelevant

        using var factory = new FakeRestClientFactory();
        using var provider = new SharePointLiveAspxDiscoveryProvider(new(
            new[] { new Uri("https://contoso.sharepoint.com/sites/a") }, "delegated-user-a",
            "declared-sites", "fixture-authority/v1", HashA), factory);
        var geo = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren.Single();
        var site = (await provider.EnumerateChildrenAsync(geo)).ObservedChildren.Single();
        var web = (await provider.EnumerateChildrenAsync(site)).ObservedChildren.Single();
        var containers = (await provider.EnumerateChildrenAsync(web)).ObservedChildren;
        var documentLibrary = containers.Single(item => item.Locator == "/sites/a/UnknownTemplateLibrary");
        var genericList = containers.Single(item => item.Locator == "/sites/a/Lists/Generic");
        var documentChildren = (await provider.EnumerateChildrenAsync(documentLibrary)).ObservedChildren;
        documentChildren.Should().Contain(item => item.SourceKind == DiscoverySourceKind.RawListLibraryFiles);
        var genericChildren = (await provider.EnumerateChildrenAsync(genericList)).ObservedChildren;
        genericChildren.Should().NotContain(item => item.SourceKind == DiscoverySourceKind.RawListLibraryFiles);
        genericChildren.Should().Contain(item => item.SourceKind == DiscoverySourceKind.ListFormBackingFiles);
        genericChildren.Should().Contain(item => item.SourceKind == DiscoverySourceKind.ListViewBackingFiles);

        var forms = genericChildren.Single(item => item.SourceKind == DiscoverySourceKind.ListFormBackingFiles);
        var batches = await ReadAllAsync(provider.CreateRawSource(forms));
        batches.SelectMany(batch => batch.Records).Should().ContainSingle(record =>
            record.FileUniqueId == FakeRestClientFactory.ResolvedFileId && record.FileName == "DispForm.aspx"); // F1

        Observation(AspxReferenceDispositions.Unknown, null, null, rawLocator: null).Disposition
            .Should().Be(AspxReferenceDispositions.Unknown); // F2
        Observation(AspxReferenceDispositions.NonAspx, null, null, rawLocator: "/view.aspx.bak").Validate()
            .Should().BeEmpty(); // F3
        Observation(AspxReferenceDispositions.Unknown, null, null,
            rawLocator: "/sites/root/Lists/X/DispForm.aspx").RawLocator.Should().StartWith("/sites/root/"); // F4
        Observation(AspxReferenceDispositions.ReferenceUnavailable, null, null, reason: "locator_not_found")
            .Validate().Should().BeEmpty(); // F5
        Observation(AspxReferenceDispositions.ReferenceUnavailable, null, null, reason: "locator_denied")
            .Validate().Should().BeEmpty(); // F6
        provider.ReferenceCollector.Build(RunId, ReferenceManifest(),
            Physical(new DiscoveryInventoryRow("forms", DiscoveryHash.Of("file", FakeRestClientFactory.ResolvedFileId),
                "/sites/a/Lists/Generic/DispForm.aspx", "DispForm.aspx", "strong", "delegated-user-a")), Registry())
            .References.Should().Contain(item => item.PermissionContext == "delegated-user-a"); // F8
        // F7 is exercised by I4: duplicate locator/object IDs remain two references to one canonical file.
    }

    [Fact]
    public void N1_N4_not_applicable_rules_require_immutable_hash_review_counterexample_and_build_binding()
    {
        var rule = Rule();
        rule.Matches("rule-2", "v1", HashA, "authority/v1", "16.0.1").Should().BeFalse(); // N1 id/hash binding
        (rule with { ApprovalRef = null }).IsValid("16.0.1").Should().BeFalse(); // N2
        (rule with { RuntimeCounterexampleState = AspxRuntimeCounterexampleState.Observed })
            .IsValid("16.0.1").Should().BeFalse(); // N3
        rule.IsValid("17.0.0").Should().BeFalse(); // N4
    }

    [Fact]
    public void Aggregate_precedence_keeps_unknown_expected_zero_and_denied_children_non_complete()
    {
        var unknownZero = Row(DiscoveryTerminalOutcome.Empty, AspxExpectedCountState.Unknown, null, 0);
        var unknownReference = Reference(new[] { unknownZero }) with { CoverageVerdict = AspxAggregateVerdict.Unknown };
        AspxAggregateEvaluator.Evaluate(Physical(), unknownReference, ReferenceManifest(), Registry(), out _)
            .Should().Be(AspxAggregateVerdict.Unknown);

        var denied = Row(DiscoveryTerminalOutcome.Denied, AspxExpectedCountState.Unknown, null, 0);
        var deniedReference = Reference(new[] { denied }) with { CoverageVerdict = AspxAggregateVerdict.Incomplete };
        AspxAggregateEvaluator.Evaluate(Physical(), deniedReference, ReferenceManifest(), Registry(), out _)
            .Should().Be(AspxAggregateVerdict.Unknown,
                "expectedCount Unknown has higher precedence than the denied failure count");
    }

    [Fact]
    public async Task Synthetic_live_provider_run_writes_separate_v2_v1_and_aggregate_volumes()
    {
        using var directory = new TemporaryDirectory();
        using var factory = new FakeRestClientFactory();
        using var provider = new SharePointLiveAspxDiscoveryProvider(new(
            new[] { new Uri("https://contoso.sharepoint.com/sites/a") }, "delegated-user-a",
            "declared-sites", "fixture-authority/v1", HashA), factory);
        var result = await new AspxAcquisitionRuntime().RunAsync(provider, new(
            directory.File("physical.sqlite"), directory.File("physical.json"),
            directory.File("reference.sqlite"), directory.File("reference.json"),
            directory.File("aggregate.json"), PhysicalManifest(), "declared_subset",
            FixtureRun: false, TenantVisibilityVerified: false, HashB, "16.0.1",
            "fixture-snapshot", Registry()));

        result.Physical.OutputVersion.Should().Be(AspxDiscoveryOutputV2.Version);
        result.Reference.OutputVersion.Should().Be(AspxReferenceOutputV1.Version);
        result.Aggregate.OutputVersion.Should().Be(AspxAcquisitionVerdictV1.Version);
        result.Physical.Inventory.Should().ContainSingle("the same resolved form is canonicalized once");
        result.Reference.References.Count(item => item.LinkedPhysicalCanonicalInventoryKey != null).Should().Be(2);
        result.Aggregate.AggregateVerdict.Should().Be(AspxAggregateVerdict.CompleteAuthorizedSurface);
        new[] { "physical.sqlite", "physical.json", "reference.sqlite", "reference.json", "aggregate.json" }
            .Should().OnlyContain(name => File.Exists(directory.File(name)));
    }

    private static AspxPaginationPageReceipt Page(int ordinal, string request, string next, bool terminal) => new(
        "scope", "authority/v1", HashA, ordinal, AspxPaginationContract.TokenHash(request), 1,
        AspxPaginationContract.TokenHash(next), HashB, terminal, DateTimeOffset.UtcNow);

    private static AspxReferenceObservation Observation(string disposition, string canonical, string fileId,
        string rawLocator = "/x.aspx", string reason = null) => new(
        "observation", AspxReferenceObservation.RequiredRecordKind, AspxReferenceSourceKinds.ListForm,
        "object", "fixture", null, rawLocator, rawLocator, null, "16.0.1", "registry/v1", HashA,
        disposition, reason, canonical, fileId,
        disposition == AspxReferenceDispositions.LinkedPhysicalGhosted ? "verified-ghosted" : "fixture",
        "fixture", Array.Empty<string>());

    private static AspxReferenceCandidate Candidate(string id, string locator, string fileId) => new(
        AspxReferenceSourceKinds.ListForm, id, "fixture", null, locator, locator, null,
        AspxReferenceDispositions.LinkedPhysicalGhosted, null, fileId, "verified-ghosted",
        "fixture", new[] { "fixture" });

    private static AspxDiscoveryOutputV2 Physical(params DiscoveryInventoryRow[] inventory) => new(
        AspxDiscoveryOutputV2.Version, RunId, DiscoveryExecutionStatus.Finished,
        DiscoveryVerdict.CompleteAuthorizedSurface, inventory, Array.Empty<DiscoveryObservationRow>(),
        Array.Empty<DiscoveryCoverageRow>(), Array.Empty<DiscoveryDenominatorRow>(), Array.Empty<string>(), 0);

    private static AspxReferenceOutputV1 Reference(IReadOnlyList<AspxSurfaceDenominatorRow> rows) => new(
        AspxReferenceOutputV1.Version, RunId, HashA, AspxAggregateVerdict.CompleteAuthorizedSurface,
        Array.Empty<AspxReferenceObservation>(), rows, Array.Empty<AspxPaginationPageReceipt>(), Array.Empty<string>());

    private static AspxReferenceRunManifest ReferenceManifest() => new(
        AspxAcquisitionVersions.ReferenceProducer, AspxAcquisitionVersions.ReferenceStore,
        "pnp/assessment@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "1111111111111111111111111111111111111111", HashA, HashB, "registry/v1", HashA,
        "16.0.1", "snapshot-1", AspxAcquisitionVersions.LiveProvider, RunId.ToString("D"));

    private static DiscoveryRunManifest PhysicalManifest() => new(
        "pnp/assessment@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "1111111111111111111111111111111111111111", DiscoveryRunManifest.CurrentContractVersion,
        DiscoveryRunManifest.CurrentSchemaVersion, HashA, HashA, HashA, HashA, HashA, HashA, HashA, HashA);

    private static AspxPlatformRegistryV1 Registry(string min = "16.0.0", string max = "16.0.9",
        IReadOnlyList<AspxPlatformRegistryEntry> entries = null) => new(
        AspxAcquisitionVersions.Registry, "registry/v1", HashA, "SPO.Core-reviewed-source",
        "repo@1111111111111111111111111111111111111111", HashB, "CCD-394-review",
        DateTimeOffset.UtcNow, "SharePointOnline", min, max, entries ?? Array.Empty<AspxPlatformRegistryEntry>());

    private static AspxPlatformRegistryEntry Entry(string id, string path, IReadOnlyList<string> aliases) => new(
        id, "SetupOrVirtual", path, aliases, "VirtualHandler", "rule", HashA, "Available", "Delegate");

    private static AspxReviewedNotApplicableRule Rule() => new(
        "rule-1", "v1", HashA, "review", "approval", "16.0.0", "16.0.9",
        "authority/v1", HashB, AspxRuntimeCounterexampleState.NoneObserved);

    private static AspxSurfaceDenominatorRow Row(DiscoveryTerminalOutcome outcome,
        AspxExpectedCountState expectedState, int? expected, int observed) => new(
        AspxAcquisitionVersions.SurfaceContract, RunId, "snapshot-1", HashA, "scope", "parent", "surface",
        AspxSurfaceApplicability.Applicable, null, null, null, null, null, null,
        AspxRuntimeCounterexampleState.NoneObserved, "authority", "https://contoso/_api/x", "v1", HashB,
        "GET", "https://contoso/_api/x", "Id", string.Empty, "fixture", "fixture",
        expected, expectedState, observed, outcome, outcome.ToString(), false, HashA, 0, null, null,
        AspxAcquisitionVersions.LiveProvider, "pnp/assessment@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "1111111111111111111111111111111111111111", "16.0.1", "registry/v1", HashA,
        RunId.ToString("D"), DateTimeOffset.UtcNow, Array.Empty<string>(), "fixture", "fixture");

    private static AspxAcquisitionVerdictV1 Envelope()
    {
        var physical = new AspxVolumeBinding(AspxDiscoveryOutputV2.Version, RunId, HashA, 10,
            "pnp/assessment@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", HashA, "snapshot-1");
        var reference = new AspxVolumeBinding(AspxReferenceOutputV1.Version, RunId, HashB, 20,
            physical.ProductRef, HashA, "snapshot-1");
        return new(AspxAcquisitionVerdictV1.Version, RunId, AspxAggregateVerdict.CompleteAuthorizedSurface,
            physical, reference, AspxAcquisitionVersions.SurfaceContract, "registry/v1", HashA, "16.0.1",
            physical.ProductRef, "1111111111111111111111111111111111111111", DateTimeOffset.UtcNow,
            Array.Empty<string>());
    }

    private static async Task<IReadOnlyList<RawDiscoveryBatch>> ReadAllAsync(IRawDiscoverySource source)
    {
        var batches = new List<RawDiscoveryBatch>();
        await foreach (var batch in source.ReadBatchesAsync()) batches.Add(batch);
        return batches;
    }

    private sealed class FakeRestClientFactory : ISharePointAspxRestClientFactory
    {
        internal const string ResolvedFileId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        private readonly FakeRestClient client = new();
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default)
        {
            client.WebUrlValue = webUrl;
            return Task.FromResult<ISharePointAspxRestClient>(client);
        }
        public void Dispose() => client.Dispose();

        private sealed class FakeRestClient : ISharePointAspxRestClient
        {
            public Uri WebUrlValue { get; set; } = new("https://contoso.sharepoint.com/sites/a");
            public Uri WebUrl => WebUrlValue;

            public Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default)
            {
                var json = requestUri.AbsoluteUri switch
                {
                    var value when value.Contains("/_api/web/webs?", StringComparison.OrdinalIgnoreCase) => "{\"value\":[]}",
                    var value when value.Contains("/RootFolder?", StringComparison.OrdinalIgnoreCase) => "{\"WelcomePage\":\"\"}",
                    var value when value.Contains("/lists?$select", StringComparison.OrdinalIgnoreCase) => """
                        {"value":[
                          {"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","Title":"UnknownTemplateLibrary","BaseType":1,"BaseTemplate":99999,"Hidden":true,"IsCatalog":false,"RootFolder":{"ServerRelativeUrl":"/sites/a/UnknownTemplateLibrary"},"DefaultViewUrl":"/x.aspx"},
                          {"Id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","Title":"Generic","BaseType":0,"BaseTemplate":100,"Hidden":false,"IsCatalog":false,"RootFolder":{"ServerRelativeUrl":"/sites/a/Lists/Generic"},"DefaultViewUrl":"/y.aspx"}
                        ]}
                        """,
                    var value when value.Contains("/Forms?", StringComparison.OrdinalIgnoreCase) =>
                        "{\"value\":[{\"Id\":\"form-1\",\"ServerRelativeUrl\":\"/sites/a/Lists/Generic/DispForm.aspx\",\"FormType\":4}]}",
                    var value when value.Contains("/Views?", StringComparison.OrdinalIgnoreCase) => "{\"value\":[]}",
                    _ => "{\"value\":[]}",
                };
                using var document = JsonDocument.Parse(json);
                var parsed = document.RootElement.TryGetProperty("value", out var valueElement) &&
                    valueElement.ValueKind == JsonValueKind.Array
                    ? valueElement.EnumerateArray().Select(value => value.Clone()).ToArray()
                    : new[] { document.RootElement.Clone() };
                return Task.FromResult(new SharePointRestPage(requestUri, HttpStatusCode.OK, parsed, null,
                    DiscoveryHash.Of(json), "fixture", DiscoveryTerminalOutcome.Complete));
            }

            public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
                CancellationToken cancellationToken = default) => Task.FromResult(new SharePointResolvedFile(
                    DiscoveryTerminalOutcome.Complete, ResolvedFileId, "DispForm.aspx", serverRelativeUrl,
                    "Customized", "fixture:file-resolution"));
            public void Dispose() { }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aspx-acquisition-v3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        internal string Path { get; }
        internal string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
