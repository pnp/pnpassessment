using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages;
#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies typed discovery roles preserve snapshot text and legacy evidence while refusing ambiguous or missing discriminators.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class DiscoveryRowTypeTests
{
    /// <summary>
    /// Enumerates physical discovery, coverage reduction and historical assessment roles without changing the legacy database column.
    /// </summary>
    [Fact]
    public void Defined_kinds_match_the_existing_producer_vocabulary_without_an_unset_or_unknown_default()
    {
        Assert.Equal(
            new[]
            {
                DiscoveryRowType.Page, DiscoveryRowType.Scope, DiscoveryRowType.Reference,
                DiscoveryRowType.Pagination, DiscoveryRowType.Gap, DiscoveryRowType.Summary,
                DiscoveryRowType.Assessment,
            },
            Enum.GetValues<DiscoveryRowType>());
        Assert.False(Enum.IsDefined(default(DiscoveryRowType)));
        Assert.Equal(
            typeof(string),
            typeof(ClassicPageDiscovery).GetProperty(nameof(ClassicPageDiscovery.RowType))!.PropertyType);
        Assert.Equal("classic-objects-v4", ClassicContentContract.InputVersion);
        Assert.Equal("classic-pages-v5", ClassicContentContract.RuleVersion);
    }

    /// <summary>
    /// Round-trips every actual role through typed JSON, immutable source storage, and the existing string-valued database column.
    /// </summary>
    [Theory]
    [InlineData("Page")]
    [InlineData("Scope")]
    [InlineData("Reference")]
    [InlineData("Pagination")]
    [InlineData("Gap")]
    [InlineData("Summary")]
    [InlineData("Assessment")]
    public async Task Existing_text_and_evidence_survive_typed_snapshot_and_database_roundtrip(string text)
    {
        DiscoveryRowType kind = text switch
        {
            "Page" => DiscoveryRowType.Page,
            "Scope" => DiscoveryRowType.Scope,
            "Reference" => DiscoveryRowType.Reference,
            "Pagination" => DiscoveryRowType.Pagination,
            "Gap" => DiscoveryRowType.Gap,
            "Summary" => DiscoveryRowType.Summary,
            "Assessment" => DiscoveryRowType.Assessment,
            _ => throw new ArgumentOutOfRangeException(nameof(text)),
        };
        using StoreCase data = new("discovery-kind-roundtrip");
        ClassicPageDiscovery legacy = LegacyEvidence(text);
        DiscoveryEvidence evidence = DiscoveryEvidenceMapper.FromLegacy(legacy);
        Assert.Equal(kind, evidence.RowType);
        string original = JsonSerializer.Serialize(legacy);
        string typedJson = JsonSerializer.Serialize(evidence);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(typedJson)));
        using JsonDocument document = JsonDocument.Parse(typedJson);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("RowType").ValueKind);
        Assert.Equal(text, document.RootElement.GetProperty("RowType").GetString());
        Assert.Equal(legacy.ScopeType, document.RootElement.GetProperty("ScopeType").GetString());
        CollectedObservation observation = new(
            Guid.NewGuid(),
            "kind:" + text,
            null,
            AcquisitionStatus.Partial,
            VersionedJson.From(new { kind = "DiscoveryEvidence" }),
            CollectionJson.Bytes(evidence),
            "Recorded incomplete evidence");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed =
            await data.UnsealedAsync([observation]);
        SnapshotInfo sealedSnapshot = await seed.Writer.SealAsync(default);
        ISnapshotReader snapshot = data.Store.SnapshotReader(seed.Assessment, seed.Snapshot);
        SourceRecord acquired = await snapshot.ReadAsync(observation.ObservationId, default);
        Assert.Equal(observation.RawBytes, acquired.Artifact.GetBytes());
        DiscoveryEvidence replayed = CollectionJson.Read<DiscoveryEvidence>(acquired.Artifact.GetBytes());
        Assert.Equal(kind, replayed.RowType);
        Assert.Equal(original, JsonSerializer.Serialize(DiscoveryEvidenceMapper.ToLegacy(replayed)));
        Assert.Equal(sealedSnapshot.ManifestDigest, (await snapshot.OpenAsync(default)).ManifestDigest);
        using ScanContext db = data.Store.CreateContext(seed.Assessment);
        ClassicPageDiscovery stored = DiscoveryEvidenceMapper.ToLegacy(replayed);
        stored.ScanId = seed.Assessment;
        db.ClassicPageDiscoveries.Add(stored);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        ClassicPageDiscovery returned = await db.ClassicPageDiscoveries.AsNoTracking().SingleAsync();
        Assert.Equal(text, returned.RowType);
        Assert.Equal(kind, DiscoveryEvidenceMapper.FromLegacy(returned).RowType);
        Assert.Equal(legacy.EvidenceJson, returned.EvidenceJson);
        Assert.Equal(legacy.DiscoveryStatus, returned.DiscoveryStatus);
        Assert.Equal(legacy.AssessmentStatus, returned.AssessmentStatus);
        Assert.Equal(
            typeof(string),
            db.Model.FindEntityType(typeof(ClassicPageDiscovery))!.FindProperty(nameof(ClassicPageDiscovery.RowType))!
                .ClrType);
    }

    /// <summary>
    /// Rejects unknown text, null, numeric values, and wrong JSON token types instead of assigning a valid evidence role.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("\"Unknown\"")]
    [InlineData("\"page\"")]
    [InlineData("\"ReferenceBinding\"")]
    [InlineData("\"\"")]
    [InlineData("\"1\"")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("999")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Invalid_wire_discriminators_fail_without_a_default_kind(string value)
    {
        byte[] source = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"value\":{\"RowType\":" + value + "}}");
        Assert.Throws<JsonException>(() => CollectionJson.Read<DiscoveryEvidence>(source));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DiscoveryEvidence>("{\"RowType\":" + value + "}"));
    }

    /// <summary>
    /// Requires the discriminator property itself and refuses programmatic unset or invalid enum values on every write boundary.
    /// </summary>
    [Fact]
    public void Missing_properties_and_unset_or_invalid_enum_values_cannot_be_serialized_as_valid_rows()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DiscoveryEvidence>("{}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebFileInventory>(
            "{\"SiteUrl\":\"https://contoso.invalid\",\"WebUrl\":\"/\",\"Evidence\":[{}],\"Files\":[]}"));
        foreach (DiscoveryRowType invalid in new[]
                 {
                     default(DiscoveryRowType), (DiscoveryRowType)(-1), (DiscoveryRowType)999
                 })
        {
            DiscoveryEvidence evidence = new() { RowType = invalid };
            Assert.Throws<JsonException>(() => JsonSerializer.Serialize(evidence));
            Assert.Throws<JsonException>(() => CollectionJson.Bytes(evidence));
            Assert.Throws<JsonException>(() => DiscoveryEvidenceMapper.ToLegacy(evidence));
        }
    }

    /// <summary>
    /// Rejects invalid legacy strings at the typed boundary rather than parsing integers or inventing an unknown or Page role.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("ScopeType")]
    [InlineData("scope")]
    [InlineData("1")]
    public void Invalid_legacy_text_fails_the_boundary_mapping(string? text)
    {
        ClassicPageDiscovery row = LegacyEvidence("Scope");
        row.RowType = text;
        Assert.Throws<JsonException>(() => DiscoveryEvidenceMapper.FromLegacy(row));
    }

    /// <summary>
    /// Exercises production source-kind dispatch and compatibility coverage with every evidence role without admitting non-Page rows as files.
    /// </summary>
    [Fact]
    public async Task Production_dispatch_and_coverage_preserve_all_kinds_without_confusing_role_with_status()
    {
        using StoreCase data = new("discovery-kind-production");
        ClassicContentFixture source = new();
        source.TransformInventory = inventory => inventory with
        {
            Evidence = inventory.Evidence.Concat(
                    new[]
                    {
                        DiscoveryRowType.Reference, DiscoveryRowType.Pagination, DiscoveryRowType.Gap,
                        DiscoveryRowType.Summary, DiscoveryRowType.Assessment,
                    }.Select((kind, ordinal) => new DiscoveryEvidence
                    {
                        ScanId = inventory.Evidence[0].ScanId,
                        SiteUrl = inventory.SiteUrl,
                        WebUrl = inventory.WebUrl,
                        RowType = kind,
                        RecordKey = "recorded-role:" + ordinal,
                        ScopeType = "Evidence",
                        DiscoveryStatus = kind == DiscoveryRowType.Gap ? "Unknown" : "Complete",
                        AssessmentStatus = "NotApplicable",
                        EvidenceJson = "{\"original\":true}",
                        ErrorCodes = kind == DiscoveryRowType.Gap ? "recorded_gap" : null,
                    }))
                .ToArray(),
        };
        PipelineCoordinator coordinator = new(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply ticket =
            await coordinator.StartPipelineAsync(new() { Collection = ClassicContentFixture.Request() });
        Guid assessmentId = Guid.Parse(ticket.AssessmentId);
        ScanStatus status = await coordinator.WaitForCompletionAsync(assessmentId).WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(status == ScanStatus.Finished, (await data.Store.LatestRootAsync(assessmentId))!.LastError);
        using ScanContext db = data.Store.CreateContext(assessmentId);
        SourceObservationRow inventory = (await db.SourceObservations.ToListAsync()).Single(row =>
            new VersionedJson(row.MetadataJson).Value.GetProperty("kind").GetString() == "FileInventory");
        WebFileInventory replayed = CollectionJson.Read<WebFileInventory>(inventory.RawBytes);
        Assert.Equal(
            Enum.GetValues<DiscoveryRowType>().OrderBy(kind => kind),
            replayed.Evidence.Select(row => row.RowType).Distinct().OrderBy(kind => kind));
        Assert.Equal(7, replayed.Evidence.Count(row => row.RowType == DiscoveryRowType.Page));
        AnalysisResultRow result = (await db.AnalysisResults.ToListAsync()).Single(row =>
            new VersionedJson(row.PayloadJson).Value.GetProperty("kind").GetString() == "FileInventory");
        Assert.Equal(AnalysisOutcome.Unknown, result.Outcome);
        JsonElement[] coverage = new VersionedJson(result.PayloadJson).Value.GetProperty("coverage")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            new[] { "Gap", "Scope" },
            coverage.Select(row => row.GetProperty("RowType").GetString()).OrderBy(kind => kind));
        List<ClassicPageDiscovery> retained = await db.ClassicPageDiscoveries.AsNoTracking().ToListAsync();
        Assert.All(
            new[] { "Reference", "Pagination", "Gap", "Summary", "Assessment" },
            kind => Assert.Contains(
                retained,
                row => row.RowType == kind && row.RecordKey.StartsWith("recorded-role:")));
        Assert.Equal("Unknown", retained.Single(row => row.RecordKey == "summary:coverage").DiscoveryStatus);
    }

    /// <summary>
    /// Supplies every legacy property, nullable fact, and original evidence text so boundary tests detect more than discriminator changes.
    /// </summary>
    private static ClassicPageDiscovery LegacyEvidence(string rowType) =>
        new()
        {
            ScanId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SiteUrl = "https://contoso.invalid/sites/recorded",
            WebUrl = "/child",
            RecordKey = "recorded:evidence",
            RowType = rowType,
            ScopeType = "Surface",
            ParentScopeKey = "scope:web",
            Url = "/sites/recorded/a.aspx",
            SiteCollectionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            WebId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ListId = null,
            FolderUniqueId = null,
            FileUniqueId = null,
            ListItemId = null,
            FileName = "a.aspx",
            PageType = "Unknown",
            ContentTypeId = null,
            SetupPath = "site templates\\sts\\default.aspx",
            CustomizedPageStatus = null,
            HomePage = null,
            LibraryHidden = null,
            ObservationMethod = "RecordedAcquisition",
            DiscoveryStatus = "Unknown",
            AssessmentStatus = "NotApplicable",
            ExpectedChildCount = null,
            ObservedChildCount = 0,
            ErrorStage = "RecordedRead",
            ErrorCodes = "recorded_gap",
            ErrorDetail = "Incomplete\r\nretained evidence",
            EvidenceJson = "{\"raw\":\" a  \",\"nullable\":null}",
            ObservedAtUtc = DateTime.UnixEpoch,
        };
}