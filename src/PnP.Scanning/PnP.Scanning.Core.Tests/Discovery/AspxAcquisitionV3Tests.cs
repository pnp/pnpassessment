using FluentAssertions;
using Microsoft.Data.Sqlite;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Process.Commands;
using System.Net;
using System.Text;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class AspxAcquisitionV3Tests
{
    [Fact]
    public void Acquisition_rest_requests_use_the_required_test_traffic_user_agent()
    {
        using var request = PnPContextSharePointAspxRestClient.CreateGetRequest(
            new Uri("https://contoso.sharepoint.com/_api/web"));

        request.Headers.UserAgent.ToString().Should().Be("testtraffic-smr");
    }

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
        var p6 = AspxPaginationContract.Validate(new[]
        {
            Page(0, null, null, terminal: true) with { AttemptCount = 2, AttemptLimit = 1 },
        }, DiscoveryTerminalOutcome.Complete);

        new[] { p1, p2, p3, p4, p6 }.Should().OnlyContain(result => result.Outcome == DiscoveryTerminalOutcome.Unknown);
        p4.OutstandingTokenCount.Should().Be(1);
        p5.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied,
            "a denied child with a null next token is not complete");
    }

    [Fact]
    public void Multi_page_receipts_hash_tokens_and_remove_raw_continuation_from_durable_evidence()
    {
        const string rawToken = "Paged=TRUE&p_ID=42";
        var nextLink = "https://contoso.sharepoint.com/_api/web/lists?$select=Id&$skiptoken=" +
            Uri.EscapeDataString(rawToken);
        var request = new Uri(nextLink);
        var page = SharePointRestResponseParser.Parse(request, HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("{\"value\":[]}"), "application/json", "request", "correlation",
            DateTimeOffset.Parse("2026-09-12T12:00:00Z"));
        var durableEndpoint = AspxDurableRequestEvidence.Endpoint(request);
        var durableEvidence = AspxDurableRequestEvidence.Reference(request, page, "Id");
        durableEndpoint.Should().Contain("$skiptoken=sha256").And.NotContain("Paged");
        durableEvidence.Should().Contain("$skiptoken=sha256").And.NotContain("Paged");

        var receipts = new[]
        {
            Page(0, null, nextLink, terminal: false),
            Page(1, nextLink, null, terminal: true) with { ActualEndpoint = durableEndpoint },
        };
        AspxPaginationContract.Validate(receipts, DiscoveryTerminalOutcome.Complete).GapCodes.Should().BeEmpty();
        receipts[0].NextTokenHash.Should().Be(receipts[1].RequestTokenHash).And.HaveLength(64);

        AspxPaginationContract.Validate(new[] { receipts[1] with { ActualEndpoint = request.AbsoluteUri } },
                DiscoveryTerminalOutcome.Complete).GapCodes
            .Should().Contain("pagination_raw_continuation_value_persisted");
    }

    [Fact]
    public void Http_success_semantic_denial_is_terminal_denied_and_never_parsed_as_items()
    {
        var uri = new Uri("https://contoso.sharepoint.com/sites/a/_api/web/webs?$select=Id");
        var login = SharePointRestResponseParser.Parse(uri, HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("<!DOCTYPE html><html><form id=\"loginForm\" action=\"https://login.microsoftonline.com/\">Sign in to your account</form></html>"),
            "text/html", "request-1", "correlation-1", DateTimeOffset.Parse("2026-09-12T12:00:00Z"));
        var accessDenied = SharePointRestResponseParser.Parse(uri, HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"-2147024891, System.UnauthorizedAccessException\",\"message\":{\"value\":\"Access denied.\"}}}"),
            "application/json", "request-2", "correlation-2", DateTimeOffset.Parse("2026-09-12T12:00:01Z"));

        login.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        login.SemanticDetectorResult.Should().Be(SharePointSemanticDetectorResults.LoginShell);
        login.Items.Should().BeEmpty();
        accessDenied.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        accessDenied.SemanticDetectorResult.Should().Be(SharePointSemanticDetectorResults.AccessDenied);
        accessDenied.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Transport_401_403_remain_terminal_denied(HttpStatusCode status)
    {
        var page = SharePointRestResponseParser.Parse(new Uri("https://contoso.sharepoint.com/_api/web"),
            status, Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Access denied\"}}"),
            "application/json");
        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        page.Items.Should().BeEmpty();
        page.ErrorCode.Should().NotBeNullOrWhiteSpace();
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
    public void Reobserved_reference_surfaces_are_reconciled_before_the_unique_store_contract()
    {
        using var directory = new TemporaryDirectory();
        var collector = new AspxReferenceCollector();
        var row = Row(DiscoveryTerminalOutcome.Complete, AspxExpectedCountState.Known, 1, 1);
        var page = Page(0, null, null, terminal: true);
        var fileId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var physical = Physical(new DiscoveryInventoryRow("scope", DiscoveryHash.Of("file", fileId),
            "/forms/shared.aspx", "shared.aspx", "strong", "fixture"));
        collector.AddSurface(row with { EvidenceRefs = new[] { "attempt-1" } }, new[] { page });
        collector.AddSurface(row with
        {
            AsOfUtc = row.AsOfUtc.AddMinutes(1),
            EvidenceRefs = new[] { "attempt-2" },
        }, new[]
        {
            page with
            {
                ReceivedAtUtc = page.ReceivedAtUtc.AddMinutes(1),
                RequestId = "request-2",
                CorrelationId = "correlation-2",
            },
        });
        collector.AddReference(Candidate("repeated", "/forms/shared.aspx", fileId));
        collector.AddReference(Candidate("repeated", "/forms/shared.aspx", fileId));

        var output = collector.Build(RunId, ReferenceManifest(), physical, Registry());

        output.Denominator.Count(item => item.SurfaceId == row.SurfaceId).Should().Be(1);
        output.Denominator.Single(item => item.SurfaceId == row.SurfaceId).EvidenceRefs
            .Should().Contain("surface-reobservation-count=2");
        output.PaginationReceipts.Should().ContainSingle();
        output.References.Should().ContainSingle();
        output.GapCodes.Should().NotContain(item => item.Contains("reobservation_conflict"));
        using var store = new AspxReferenceStore(directory.File("reference.sqlite"));
        var action = () => store.Write(ReferenceManifest(), output, resume: false);
        action.Should().NotThrow("equivalent rescans are one logical denominator surface");
    }

    [Fact]
    public void Conflicting_surface_reobservation_fails_closed_instead_of_crashing_the_store()
    {
        using var directory = new TemporaryDirectory();
        var collector = new AspxReferenceCollector();
        var first = Row(DiscoveryTerminalOutcome.Complete, AspxExpectedCountState.Known, 1, 1);
        collector.AddSurface(first, Array.Empty<AspxPaginationPageReceipt>());
        collector.AddSurface(first with
        {
            ExpectedCount = 2,
            ObservedCount = 2,
            PaginationChainHash = HashB,
            AsOfUtc = first.AsOfUtc.AddMinutes(1),
        }, Array.Empty<AspxPaginationPageReceipt>());

        var output = collector.Build(RunId, ReferenceManifest(), Physical(), Registry());
        var reconciled = output.Denominator.Single(item => item.SurfaceId == first.SurfaceId);

        reconciled.TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Unknown);
        reconciled.ExpectedCountState.Should().Be(AspxExpectedCountState.Unknown);
        reconciled.ExpectedCount.Should().BeNull();
        output.GapCodes.Should().Contain("reference_surface_reobservation_conflict:" + first.SurfaceId);
        using var store = new AspxReferenceStore(directory.File("reference.sqlite"));
        var action = () => store.Write(ReferenceManifest(), output, resume: false);
        action.Should().NotThrow("conflicts remain explicit evidence instead of violating the SQLite key");
    }

    [Fact]
    public void V1_V7_exact_version_and_companion_binding_reject_legacy_or_drifted_composition()
    {
        AspxAcquisitionEnvelopeValidator.PhysicalOnly(AspxDiscoveryOutputV2.Version)
            .Should().Match<AspxAcquisitionEnvelopeValidation>(value => value.PhysicalOnly &&
                value.Verdict == AspxAggregateVerdict.Unknown); // V1
        AspxAcquisitionEnvelopeValidator.Validate(null, HashA, HashB,
            AspxDiscoveryOutputV2.Version, AspxReferenceOutputV2.Version).GapCodes
            .Should().Contain("acquisition_envelope_missing"); // V2

        var envelope = Envelope();
        AspxAcquisitionEnvelopeValidator.Validate(envelope, HashB, HashB,
            AspxDiscoveryOutputV2.Version, AspxReferenceOutputV2.Version).GapCodes
            .Should().Contain("physical_hash_mismatch"); // V3
        Observation("future-disposition", null, null).Validate().Should().Contain("unknown_disposition"); // V4

        var mixedFence = envelope with
        {
            ReferenceVolume = envelope.ReferenceVolume with { SnapshotFence = "other-fence" },
        };
        AspxAcquisitionEnvelopeValidator.Validate(mixedFence, HashA, HashB,
            AspxDiscoveryOutputV2.Version, AspxReferenceOutputV2.Version).GapCodes
            .Should().Contain("snapshot_fence_mismatch"); // V5

        var mixedProduct = envelope with
        {
            ReferenceVolume = envelope.ReferenceVolume with
            {
                ProductRef = "pnp/assessment@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            },
        };
        AspxAcquisitionEnvelopeValidator.Validate(mixedProduct, HashA, HashB,
            AspxDiscoveryOutputV2.Version, AspxReferenceOutputV2.Version).GapCodes
            .Should().Contain("product_ref_mismatch"); // V6
        AspxAcquisitionEnvelopeValidator.Validate(envelope, HashA, HashB,
            AspxDiscoveryOutputV2.Version, "aspx-discovery-output/v2").GapCodes
            .Should().Contain("reference_version_incompatible"); // V7
    }

    [Fact]
    public async Task Terminal_receipt_preserves_null_nonzero_zero_semantics_and_binds_all_official_volumes()
    {
        using var directory = new TemporaryDirectory();
        var executablePath = directory.File("microsoft365-assessment.dll");
        await File.WriteAllTextAsync(executablePath, "managed-executable");
        var executableFile = await AspxAggregateEvaluator.HashFileAsync(executablePath);
        var executable = new AspxManagedExecutableBinding("microsoft365-assessment", "1.13.0", "1.13.0",
            Path.GetFileName(executablePath), executableFile.Hash, executableFile.Length, HashA, 3,
            null, null, null);
        var nullExit = new AspxTerminalRunReceiptV1(AspxTerminalRunReceiptV1.Version, RunId, null,
            "Unknown", "pnp/assessment@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1111111111111111111111111111111111111111", "snapshot-1", null, executable,
            Array.Empty<AspxTerminalVolumeBinding>(), DateTimeOffset.UtcNow, null, null);
        AspxTerminalRunReceiptValidator.Validate(nullExit).GapCodes.Should().Contain("terminal_exit_code_missing");

        var failedPath = directory.File("failed-terminal.json");
        var failed = await AspxTerminalRunReceiptWriter.WriteAsync(failedPath, RunId, 2, "Cancelled",
            null, null, "snapshot-1", null, executable, Array.Empty<AspxTerminalOutputSpec>(),
            "operation_cancelled", HashB);
        failed.ExitCode.Should().Be(2);
        (await AspxTerminalRunReceiptValidator.ReadAndValidateAsync(failedPath)).ExitCode.Should().Be(2);

        var outputs = await CreateOfficialVolumesAsync(directory);
        var successPath = directory.File("success-terminal.json");
        var success = await AspxTerminalRunReceiptWriter.WriteAsync(successPath, RunId, 0, "Succeeded",
            "pnp/assessment@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1111111111111111111111111111111111111111", "snapshot-1", "Incomplete",
            executable, outputs);
        success.ExitCode.Should().Be(0);
        success.Volumes.Should().HaveCount(5);
        AspxTerminalRunReceiptValidator.Validate(success).Valid.Should().BeTrue();
        (await AspxTerminalRunReceiptValidator.ReadAndValidateAsync(successPath, outputs)).Volumes
            .Should().OnlyContain(volume => volume.Length > 0 && volume.Sha256.Length == 64);
    }

    [Fact]
    public async Task Terminal_fresh_readback_rejects_missing_swapped_truncated_and_wrong_version_volumes()
    {
        using var directory = new TemporaryDirectory();
        var executablePath = directory.File("microsoft365-assessment.dll");
        await File.WriteAllTextAsync(executablePath, "managed-executable");
        var executableFile = await AspxAggregateEvaluator.HashFileAsync(executablePath);
        var executable = new AspxManagedExecutableBinding("microsoft365-assessment", "1.13.0", "1.13.0",
            Path.GetFileName(executablePath), executableFile.Hash, executableFile.Length, HashA, 3,
            null, null, null);
        var outputs = await CreateOfficialVolumesAsync(directory);
        var receiptPath = directory.File("terminal.json");
        var receipt = await AspxTerminalRunReceiptWriter.WriteAsync(receiptPath, RunId, 0, "Succeeded",
            "pnp/assessment@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1111111111111111111111111111111111111111", "snapshot-1", "Unknown",
            executable, outputs);

        var missingPath = outputs.Single(output => output.Role == AspxTerminalVolumeRoles.PhysicalOutput).Path;
        var original = await File.ReadAllBytesAsync(missingPath);
        File.Delete(missingPath);
        var missing = () => AspxTerminalRunReceiptValidator.ReadAndValidateAsync(receiptPath, outputs);
        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal_official_volume_missing:physical-output*");
        await File.WriteAllBytesAsync(missingPath, original);

        await File.WriteAllBytesAsync(missingPath, original[..Math.Max(1, original.Length / 2)]);
        var truncated = () => AspxTerminalRunReceiptValidator.ReadAndValidateAsync(receiptPath, outputs);
        await truncated.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal_official_volume_hash_mismatch:physical-output*");
        await File.WriteAllBytesAsync(missingPath, original);

        var swappedVolumes = receipt.Volumes.Select(volume => volume.Role switch
        {
            AspxTerminalVolumeRoles.PhysicalOutput => volume with
            {
                FileName = receipt.Volumes.Single(item => item.Role == AspxTerminalVolumeRoles.ReferenceOutput).FileName,
            },
            AspxTerminalVolumeRoles.ReferenceOutput => volume with
            {
                FileName = receipt.Volumes.Single(item => item.Role == AspxTerminalVolumeRoles.PhysicalOutput).FileName,
            },
            _ => volume,
        }).ToArray();
        var swappedPath = directory.File("terminal-swapped.json");
        await File.WriteAllTextAsync(swappedPath, System.Text.Json.JsonSerializer.Serialize(
            receipt with { Volumes = swappedVolumes }, AspxInventoryRuntime.JsonOptions(indented: true)));
        var swapped = () => AspxTerminalRunReceiptValidator.ReadAndValidateAsync(swappedPath, outputs);
        await swapped.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal_official_file_name_mismatch:physical-output*");

        var wrongVersion = receipt with
        {
            Volumes = receipt.Volumes.Select(volume => volume.Role == AspxTerminalVolumeRoles.AggregateOutput
                ? volume with { OutputVersion = "fixture/v1" } : volume).ToArray(),
        };
        AspxTerminalRunReceiptValidator.Validate(wrongVersion).GapCodes.Should()
            .Contain("terminal_volume_version_incompatible:aggregate-output");
    }

    [Fact]
    public void Cli_contract_requires_terminal_receipt_and_advertises_v2_companion_outputs()
    {
        var command = AspxAcquisitionCommandDefinition.Create((_, _) => Task.FromResult(0));
        command.Options.Select(option => option.Name).Should().Contain("terminal-receipt");
        command.Options.Select(option => option.Name).Should().Contain("scope-mode");
        command.Description.Should().Contain("reference v2").And.Contain("aggregate v2")
            .And.Contain("terminal receipt v1");
    }

    [Fact]
    public async Task Product_tenant_authority_freezes_independent_site_and_subweb_denominator()
    {
        var adapter = new FakeTenantAuthorityAdapter();
        var tenant = new Uri("https://contoso.sharepoint.com");
        var first = await AspxTenantAuthorityCapture.CaptureAsync(AspxScopeModes.ProductTenantAuthority,
            tenant, Array.Empty<Uri>(), adapter);
        var second = await AspxTenantAuthorityCapture.CaptureAsync(AspxScopeModes.ProductTenantAuthority,
            tenant, Array.Empty<Uri>(), adapter);

        first.ContractVersion.Should().Be(AspxTenantAuthoritySnapshot.CurrentContractVersion);
        first.AuthorityHash.Should().HaveLength(64).And.Be(second.AuthorityHash,
            "UTC receipt times are evidence metadata, not scope identity");
        first.TenantVisibilityVerified.Should().BeTrue();
        first.Sites.Provider.Should().Be(PnPCoreAspxTenantAuthorityAdapter.SiteProvider);
        first.Sites.ActualFilter.Should().Be(PnPCoreAspxTenantAuthorityAdapter.SiteFilter);
        first.SiteWebs.Single().Webs.ActualFilter.Should().Be(PnPCoreAspxTenantAuthorityAdapter.WebFilter);
        first.SiteWebs.Single().Webs.Items.Single(web => !web.IsRootWeb).ParentWebUrl.Should()
            .Be(new Uri("https://contoso.sharepoint.com/sites/a"));
        adapter.SiteEnumerationCount.Should().Be(2);
        adapter.WebEnumerationCount.Should().Be(2);
    }

    [Fact]
    public async Task Declared_subset_never_sets_tenant_visibility_even_when_subweb_authority_closes()
    {
        var adapter = new FakeTenantAuthorityAdapter();
        var snapshot = await AspxTenantAuthorityCapture.CaptureAsync(AspxScopeModes.DeclaredSubset,
            new Uri("https://contoso.sharepoint.com"),
            new[] { new Uri("https://contoso.sharepoint.com/sites/a") }, adapter);

        snapshot.TenantVisibilityVerified.Should().BeFalse();
        snapshot.Sites.Provider.Should().Be("Assessment.CLI");
        snapshot.Sites.Exclusions.Should().Contain("tenant-site-denominator-not-enumerated");
        adapter.SiteEnumerationCount.Should().Be(0, "declared sites cannot prove tenant visibility");
        adapter.WebEnumerationCount.Should().Be(1, "declared sites still use the product subweb authority adapter");
    }

    [Fact]
    public async Task Denied_subweb_authority_is_persisted_per_site_and_cannot_become_complete()
    {
        var adapter = new FakeTenantAuthorityAdapter(denyWebs: true);
        var snapshot = await AspxTenantAuthorityCapture.CaptureAsync(AspxScopeModes.ProductTenantAuthority,
            new Uri("https://contoso.sharepoint.com"), Array.Empty<Uri>(), adapter);
        snapshot.TenantVisibilityVerified.Should().BeFalse();

        using var factory = new FakeRestClientFactory();
        using var provider = new SharePointLiveAspxDiscoveryProvider(new(
            snapshot.Sites.Items.Select(site => site.Url).ToArray(), "sites-read-all-app",
            "authorized-tenant", snapshot.AuthorityRevision, snapshot.AuthorityHash,
            "16.0.27709.12000", snapshot), factory);
        var geo = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren.Single();
        var site = (await provider.EnumerateChildrenAsync(geo)).ObservedChildren.Single();
        var webResult = await provider.EnumerateChildrenAsync(site);
        webResult.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        webResult.ObservedChildren.Should().ContainSingle("the known root remains scannable");

        var output = provider.ReferenceCollector.Build(RunId,
            ReferenceManifest() with { ScopeAuthorityHash = snapshot.AuthorityHash }, Physical(), Registry());
        var receipt = output.Denominator.Single(row => row.RequiredAdapter == "ProductRootAndSubwebAuthority");
        receipt.TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        receipt.ExpectedCountState.Should().Be(AspxExpectedCountState.Unknown);
        receipt.ExpectedCount.Should().BeNull();
        receipt.ActualEndpoint.Should().Contain("GetSiteCollectionWebsWithDetailsAsync");
        output.CoverageVerdict.Should().NotBe(AspxAggregateVerdict.CompleteAuthorizedSurface);
    }

    [Fact]
    public async Task Modeled_welcome_page_and_web_root_denials_remain_explicit_fail_closed_surfaces()
    {
        var snapshot = await AspxTenantAuthorityCapture.CaptureAsync(AspxScopeModes.ProductTenantAuthority,
            new Uri("https://contoso.sharepoint.com"), Array.Empty<Uri>(), new FakeTenantAuthorityAdapter());
        using var factory = new FakeRestClientFactory(DiscoveryTerminalOutcome.Denied,
            DiscoveryTerminalOutcome.Denied);
        using var provider = new SharePointLiveAspxDiscoveryProvider(new(
            snapshot.Sites.Items.Select(site => site.Url).ToArray(), "sites-read-all-app",
            "authorized-tenant", snapshot.AuthorityRevision, snapshot.AuthorityHash,
            "16.0.27709.12000", snapshot), factory);
        var geo = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren.Single();
        var site = (await provider.EnumerateChildrenAsync(geo)).ObservedChildren.Single();
        var web = (await provider.EnumerateChildrenAsync(site)).ObservedChildren.Single(item =>
            item.Locator == "https://contoso.sharepoint.com/sites/a");
        var containers = (await provider.EnumerateChildrenAsync(web)).ObservedChildren;
        var webRoot = containers.Single(item => item.Locator == "https://contoso.sharepoint.com/sites/a");
        var webRootFolder = (await provider.EnumerateChildrenAsync(webRoot)).ObservedChildren.Single();
        (await ReadAllAsync(provider.CreateRawSource(webRootFolder))).Should().ContainSingle(batch =>
            batch.TerminalOutcome == DiscoveryTerminalOutcome.Denied);
        (await provider.EnumerateChildrenAsync(webRootFolder)).Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);

        var output = provider.ReferenceCollector.Build(RunId,
            ReferenceManifest() with { ScopeAuthorityHash = snapshot.AuthorityHash }, Physical(), Registry());
        output.Denominator.Single(row => row.RequiredAdapter == "PnPCoreWelcomePageAuthority")
            .TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        output.Denominator.Single(row => row.RequiredAdapter == "PnPCoreWebRootFilesAuthority")
            .TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        output.References.Should().Contain(item => item.SourceKind == AspxReferenceSourceKinds.WebWelcomePage &&
            item.Disposition == AspxReferenceDispositions.ReferenceUnavailable);
        output.CoverageVerdict.Should().NotBe(AspxAggregateVerdict.CompleteAuthorizedSurface);
    }

    [Fact]
    public void Reference_resume_rejects_v1_contract_before_mutating_the_ledger()
    {
        using var directory = new TemporaryDirectory();
        var database = directory.File("reference.sqlite");
        var manifest = ReferenceManifest();
        var output = new AspxReferenceCollector().Build(RunId, manifest, Physical(), Registry());
        using (var store = new AspxReferenceStore(database)) store.Write(manifest, output, resume: false);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ReferenceRuns SET OutputVersion='aspx-reference-output/v1' WHERE RunId=$runId";
            command.Parameters.AddWithValue("$runId", RunId.ToString("D"));
            command.ExecuteNonQuery();
        }

        using var resumed = new AspxReferenceStore(database);
        var action = () => resumed.ValidateResumeCompatibility(RunId, manifest);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*v1 receipts cannot be resumed as v2*");
    }

    [Fact]
    public void Reference_resume_accepts_equivalent_build_but_rejects_registry_drift()
    {
        using var directory = new TemporaryDirectory();
        var database = directory.File("reference.sqlite");
        var baseline = ReferenceManifest();
        var collector = new AspxReferenceCollector();
        using var store = new AspxReferenceStore(database);
        store.Write(baseline, collector.Build(RunId, baseline, Physical(), Registry()), resume: false);

        var equivalentBuild = baseline with
        {
            ProductRef = "pnp/assessment@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            SdkRef = "2222222222222222222222222222222222222222",
            PlatformBuildRef = "16.0.2",
        };
        var equivalentWrite = () => store.Write(equivalentBuild,
            collector.Build(RunId, equivalentBuild, Physical(), Registry()), resume: true);
        equivalentWrite.Should().NotThrow();

        var registryDrift = equivalentBuild with { RegistryHash = HashB };
        var incompatible = () => store.ValidateResumeCompatibility(RunId, registryDrift);
        incompatible.Should().Throw<InvalidOperationException>().WithMessage("*RegistryHash*");
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
            "declared-sites", "fixture-authority/v1", HashA, "16.0.27709.12000"), factory);
        var geo = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren.Single();
        var site = (await provider.EnumerateChildrenAsync(geo)).ObservedChildren.Single();
        var web = (await provider.EnumerateChildrenAsync(site)).ObservedChildren.Single();
        var containers = (await provider.EnumerateChildrenAsync(web)).ObservedChildren;
        var documentLibrary = containers.Single(item => item.Locator == "/sites/a/UnknownTemplateLibrary");
        var genericList = containers.Single(item => item.Locator == "/sites/a/Lists/Generic");
        var userInformationList = containers.Single(item => item.Locator == "/sites/a/_catalogs/users");
        var documentChildren = (await provider.EnumerateChildrenAsync(documentLibrary)).ObservedChildren;
        documentChildren.Should().Contain(item => item.SourceKind == DiscoverySourceKind.RawListLibraryFiles);
        var genericChildren = (await provider.EnumerateChildrenAsync(genericList)).ObservedChildren;
        genericChildren.Should().NotContain(item => item.SourceKind == DiscoverySourceKind.RawListLibraryFiles &&
            item.Locator == genericList.Locator, "non-library list roots are not document-library file surfaces");
        genericChildren.Should().Contain(item => item.SourceKind == DiscoverySourceKind.RawListLibraryFiles &&
            item.Locator.EndsWith("/Forms", StringComparison.OrdinalIgnoreCase),
            "the physical Forms tree is an independent required surface for every list with a root folder");
        genericChildren.Should().Contain(item => item.SourceKind == DiscoverySourceKind.ListFormBackingFiles);
        genericChildren.Should().Contain(item => item.SourceKind == DiscoverySourceKind.ListViewBackingFiles);

        var forms = genericChildren.Single(item => item.SourceKind == DiscoverySourceKind.ListFormBackingFiles);
        var batches = await ReadAllAsync(provider.CreateRawSource(forms));
        batches.SelectMany(batch => batch.Records).Should().ContainSingle(record =>
            record.FileUniqueId == FakeRestClientFactory.ResolvedFileId && record.FileName == "DispForm.aspx"); // F1

        var userForms = (await provider.EnumerateChildrenAsync(userInformationList)).ObservedChildren
            .Single(item => item.SourceKind == DiscoverySourceKind.ListFormBackingFiles);
        var userBatches = await ReadAllAsync(provider.CreateRawSource(userForms));
        userBatches.Should().ContainSingle(batch => batch.TerminalOutcome == DiscoveryTerminalOutcome.Failed);
        var systemOutput = provider.ReferenceCollector.Build(RunId, ReferenceManifest(), Physical(), Registry());
        var systemRow = systemOutput.Denominator.Single(row => row.RequiredAdapter == "AllListFormsAuthority" &&
            row.ApplicabilityRuleId == AspxSystemListFormsPolicy.RuleId);
        systemRow.TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        systemRow.ExpectedCountState.Should().Be(AspxExpectedCountState.Unknown);
        systemRow.ExpectedCount.Should().BeNull();
        systemRow.Applicability.Should().Be(AspxSurfaceApplicability.SystemOrVirtualOnly);
        systemRow.ClassificationEffect.Should().Contain("historical-virtual-unknown");
        systemOutput.References.Should().Contain(item =>
            item.ReasonCode == AspxSystemListFormsPolicy.ReasonCode &&
            item.Disposition == AspxReferenceDispositions.ReferenceUnavailable);
        var systemReceipt = systemOutput.PaginationReceipts.Single(receipt =>
            receipt.ActualEndpoint.Contains("dddddddd-dddd-dddd-dddd-dddddddddddd", StringComparison.OrdinalIgnoreCase) &&
            receipt.ActualEndpoint.Contains("/Forms?", StringComparison.OrdinalIgnoreCase));
        systemReceipt.ReceiptVersion.Should().Be(AspxAcquisitionVersions.PaginationReceipt);
        systemReceipt.ActualMethod.Should().Be("GET");
        systemReceipt.ActualSelect.Should().Be("Id,ServerRelativeUrl,FormType");
        systemReceipt.ActualFilter.Should().BeEmpty();
        systemReceipt.HttpStatusCode.Should().Be(400);
        systemReceipt.SemanticDetectorResult.Should().Be(SharePointSemanticDetectorResults.ErrorEnvelope);
        systemReceipt.AttemptCount.Should().Be(1);
        systemReceipt.AttemptLimit.Should().Be(1);
        systemReceipt.RequestId.Should().Be("fixture-request-id");
        systemReceipt.CorrelationId.Should().Be("fixture-correlation-id");
        systemReceipt.ErrorCode.Should().Be("-1, Microsoft.SharePoint.SPException");
        systemReceipt.ReceivedAtUtc.Should().NotBe(default);

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
    public async Task Synthetic_live_provider_run_writes_separate_v2_volumes()
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
        result.Reference.OutputVersion.Should().Be(AspxReferenceOutputV2.Version);
        result.Aggregate.OutputVersion.Should().Be(AspxAcquisitionVerdictV2.Version);
        result.Physical.Inventory.Should().ContainSingle("the same resolved form is canonicalized once");
        result.Reference.References.Count(item => item.LinkedPhysicalCanonicalInventoryKey != null).Should().Be(2);
        result.Aggregate.AggregateVerdict.Should().Be(AspxAggregateVerdict.Unknown,
            "the retained _catalogs/users Forms failure has expectedCount Unknown");
        new[] { "physical.sqlite", "physical.json", "reference.sqlite", "reference.json", "aggregate.json" }
            .Should().OnlyContain(name => File.Exists(directory.File(name)));
    }

    private static AspxPaginationPageReceipt Page(int ordinal, string request, string next, bool terminal) => new(
        AspxAcquisitionVersions.PaginationReceipt, "scope", "authority/v1", HashA, "GET",
        "https://contoso.sharepoint.com/_api/web/lists?$select=Id", "Id", string.Empty, ordinal,
        AspxPaginationContract.TokenHash(request), 1, AspxPaginationContract.TokenHash(next), HashB,
        200, SharePointSemanticDetectorResults.None, 1, 1, "request", "correlation", null,
        terminal, DateTimeOffset.UtcNow);

    private static async Task<IReadOnlyList<AspxTerminalOutputSpec>> CreateOfficialVolumesAsync(
        TemporaryDirectory directory)
    {
        using var factory = new FakeRestClientFactory();
        using var provider = new SharePointLiveAspxDiscoveryProvider(new(
            new[] { new Uri("https://contoso.sharepoint.com/sites/a") }, "delegated-user-a",
            "declared-sites", "fixture-authority/v1", HashA, "16.0.1"), factory);
        await new AspxAcquisitionRuntime().RunAsync(provider, new(
            directory.File("physical.sqlite"), directory.File("physical.json"),
            directory.File("reference.sqlite"), directory.File("reference.json"),
            directory.File("aggregate.json"), PhysicalManifest(), "declared_subset",
            FixtureRun: false, TenantVisibilityVerified: false, HashB, "16.0.1",
            "snapshot-1", Registry(), NewRunId: RunId));
        return new[]
        {
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.PhysicalDatabase,
                DiscoveryRunManifest.CurrentSchemaVersion, directory.File("physical.sqlite")),
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.PhysicalOutput,
                AspxDiscoveryOutputV2.Version, directory.File("physical.json")),
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.ReferenceDatabase,
                AspxAcquisitionVersions.ReferenceStore, directory.File("reference.sqlite")),
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.ReferenceOutput,
                AspxReferenceOutputV2.Version, directory.File("reference.json")),
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.AggregateOutput,
                AspxAcquisitionVerdictV2.Version, directory.File("aggregate.json")),
        };
    }

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

    private static AspxReferenceOutputV2 Reference(IReadOnlyList<AspxSurfaceDenominatorRow> rows) => new(
        AspxReferenceOutputV2.Version, RunId, HashA, AspxAggregateVerdict.CompleteAuthorizedSurface,
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

    private static AspxAcquisitionVerdictV2 Envelope()
    {
        var physical = new AspxVolumeBinding(AspxDiscoveryOutputV2.Version, RunId, HashA, 10,
            "pnp/assessment@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", HashA, "snapshot-1");
        var reference = new AspxVolumeBinding(AspxReferenceOutputV2.Version, RunId, HashB, 20,
            physical.ProductRef, HashA, "snapshot-1");
        return new(AspxAcquisitionVerdictV2.Version, RunId, AspxAggregateVerdict.CompleteAuthorizedSurface,
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
        private readonly FakeRestClient client;
        internal FakeRestClientFactory(DiscoveryTerminalOutcome welcomeOutcome = DiscoveryTerminalOutcome.Complete,
            DiscoveryTerminalOutcome folderOutcome = DiscoveryTerminalOutcome.Empty) =>
            client = new FakeRestClient(welcomeOutcome, folderOutcome);
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default)
        {
            client.WebUrlValue = webUrl;
            return Task.FromResult<ISharePointAspxRestClient>(client);
        }
        public void Dispose() => client.Dispose();

        private sealed class FakeRestClient : ISharePointAspxRestClient
        {
            private readonly DiscoveryTerminalOutcome welcomeOutcome;
            private readonly DiscoveryTerminalOutcome folderOutcome;
            internal FakeRestClient(DiscoveryTerminalOutcome welcomeOutcome,
                DiscoveryTerminalOutcome folderOutcome)
            {
                this.welcomeOutcome = welcomeOutcome;
                this.folderOutcome = folderOutcome;
            }
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
                          {"Id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","Title":"Generic","BaseType":0,"BaseTemplate":100,"Hidden":false,"IsCatalog":false,"RootFolder":{"ServerRelativeUrl":"/sites/a/Lists/Generic"},"DefaultViewUrl":"/y.aspx"},
                          {"Id":"dddddddd-dddd-dddd-dddd-dddddddddddd","Title":"User Information List","BaseType":0,"BaseTemplate":112,"Hidden":true,"IsCatalog":false,"RootFolder":{"ServerRelativeUrl":"/sites/a/_catalogs/users"},"DefaultViewUrl":"/_catalogs/users/simple.aspx"}
                        ]}
                        """,
                    var value when value.Contains("dddddddd-dddd-dddd-dddd-dddddddddddd", StringComparison.OrdinalIgnoreCase) &&
                        value.Contains("/Forms?", StringComparison.OrdinalIgnoreCase) =>
                        "{\"error\":{\"code\":\"-1, Microsoft.SharePoint.SPException\",\"message\":{\"value\":\"The requested operation is not supported for this list.\"}}}",
                    var value when value.Contains("/Forms?", StringComparison.OrdinalIgnoreCase) =>
                        "{\"value\":[{\"Id\":\"form-1\",\"ServerRelativeUrl\":\"/sites/a/Lists/Generic/DispForm.aspx\",\"FormType\":4}]}",
                    var value when value.Contains("/Views?", StringComparison.OrdinalIgnoreCase) => "{\"value\":[]}",
                    _ => "{\"value\":[]}",
                };
                var status = requestUri.AbsoluteUri.Contains("dddddddd-dddd-dddd-dddd-dddddddddddd", StringComparison.OrdinalIgnoreCase) &&
                    requestUri.AbsoluteUri.Contains("/Forms?", StringComparison.OrdinalIgnoreCase)
                    ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
                return Task.FromResult(SharePointRestResponseParser.Parse(requestUri, status,
                    Encoding.UTF8.GetBytes(json), "application/json", "fixture-request-id",
                    "fixture-correlation-id", DateTimeOffset.Parse("2026-09-12T12:00:00Z")));
            }

            public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
                CancellationToken cancellationToken = default) => Task.FromResult(new SharePointResolvedFile(
                    DiscoveryTerminalOutcome.Complete, ResolvedFileId, "DispForm.aspx", serverRelativeUrl,
                    "Customized", "fixture:file-resolution"));
            public Task<SharePointModeledValue> ReadWelcomePageAsync(
                CancellationToken cancellationToken = default) => Task.FromResult(new SharePointModeledValue(
                    welcomeOutcome, welcomeOutcome == DiscoveryTerminalOutcome.Complete ? string.Empty : null,
                    "fixture:pnp-modeled-web", "GetAsync(WelcomePage)",
                    welcomeOutcome == DiscoveryTerminalOutcome.Denied ? "welcome_page_denied" : null,
                    "fixture:welcome-page", DateTimeOffset.UtcNow));
            public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl,
                CancellationToken cancellationToken = default) => Task.FromResult(new SharePointModeledFolderResult(
                    folderOutcome, "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", serverRelativeUrl,
                    Array.Empty<SharePointModeledFolder>(), Array.Empty<SharePointModeledFile>(),
                    "fixture:pnp-modeled-folder", "GetFolder(Folders,Files)",
                    folderOutcome == DiscoveryTerminalOutcome.Denied ? "web_root_folder_denied" : null,
                    "fixture:web-root", DateTimeOffset.UtcNow));
            public void Dispose() { }
        }
    }

    private sealed class FakeTenantAuthorityAdapter : IAspxTenantAuthorityAdapter
    {
        private readonly bool denyWebs;
        internal FakeTenantAuthorityAdapter(bool denyWebs = false) => this.denyWebs = denyWebs;
        internal int SiteEnumerationCount { get; private set; }
        internal int WebEnumerationCount { get; private set; }

        public Task<AspxAuthorityCollection<AspxAuthoritySite>> EnumerateSiteCollectionsAsync(
            Uri tenantRoot, CancellationToken cancellationToken = default)
        {
            SiteEnumerationCount++;
            var sites = new[]
            {
                new AspxAuthoritySite(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    new Uri("https://contoso.sharepoint.com/sites/a"), "graph-a", "Site A"),
            };
            return Task.FromResult(new AspxAuthorityCollection<AspxAuthoritySite>(
                DiscoveryTerminalOutcome.Complete, sites, PnPCoreAspxTenantAuthorityAdapter.SiteProvider,
                PnPCoreAspxTenantAuthorityAdapter.SiteOperation, PnPCoreAspxTenantAuthorityAdapter.SiteFilter,
                Array.Empty<string>(), null, null, ContinuationRemaining: false, DateTimeOffset.UtcNow));
        }

        public Task<AspxAuthorityCollection<AspxAuthorityWeb>> EnumerateWebsAsync(
            AspxAuthoritySite site, CancellationToken cancellationToken = default)
        {
            WebEnumerationCount++;
            var root = new AspxAuthorityWeb(site.RootWebId, site.Url, "/sites/a", null, "STS#3", true);
            if (denyWebs)
                return Task.FromResult(new AspxAuthorityCollection<AspxAuthorityWeb>(
                    DiscoveryTerminalOutcome.Denied, new[] { root },
                    PnPCoreAspxTenantAuthorityAdapter.WebProvider,
                    PnPCoreAspxTenantAuthorityAdapter.WebOperation, PnPCoreAspxTenantAuthorityAdapter.WebFilter,
                    Array.Empty<string>(), "root_and_subweb_authority_denied", "fixture 403",
                    ContinuationRemaining: false, DateTimeOffset.UtcNow));
            var subweb = new AspxAuthorityWeb(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                new Uri("https://contoso.sharepoint.com/sites/a/sub"), "/sites/a/sub", site.Url, "STS#3", false);
            return Task.FromResult(new AspxAuthorityCollection<AspxAuthorityWeb>(
                DiscoveryTerminalOutcome.Complete, new[] { root, subweb },
                PnPCoreAspxTenantAuthorityAdapter.WebProvider,
                PnPCoreAspxTenantAuthorityAdapter.WebOperation, PnPCoreAspxTenantAuthorityAdapter.WebFilter,
                Array.Empty<string>(), null, null, ContinuationRemaining: false, DateTimeOffset.UtcNow));
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
