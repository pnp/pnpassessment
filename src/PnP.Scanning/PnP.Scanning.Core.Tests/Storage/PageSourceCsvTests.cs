using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

[Trait("Category", "PageInherits")]
public sealed class PageSourceCsvTests
{
    // Deliberately hand-listed. Do not derive this contract from CsvHelper, reflection or the product map.
    internal static readonly string[] OriginalHeaders =
    {
        "RecordKey", "RowType", "ScopeType", "ParentScopeKey", "Url", "SiteCollectionId", "WebId", "ListId",
        "FolderUniqueId", "FileUniqueId", "ListItemId", "FileName", "AssetPurpose", "AssetPurposeStatus",
        "AssetPurposeReason", "ContentTypeId", "DeclaredPageType", "ResolvedPageType", "PageTypeEvidenceOrigin",
        "PageTypeSourceStatus", "PageTypeResolutionStatus", "PublishingLayoutFamily", "PageTypeReason",
        "PageTypeEvidenceJson", "HomePage", "LibraryHidden", "ObservationMethod", "DiscoveryStatus",
        "AssessmentStatus", "ExpectedChildCount", "ObservedChildCount", "ErrorStage", "ErrorCodes", "ErrorDetail",
        "EvidenceJson", "ObservedAtUtc", "ScanId", "SiteUrl", "WebUrl",
    };

    internal static readonly string[] AddedHeaders =
    {
        "SourceEvidenceJson", "SourceEvidenceState", "OriginalDiscoveryUrl", "OriginalDiscoveryFileName",
        "OriginalDiscoveryObservedAtUtc", "SourceObservationId", "SourceSiteCollectionId", "SourceWebId",
        "SourceFileUniqueId", "SourceListId", "SourceListItemId", "SourceUrl", "SourceFileName", "SourceIdentityState",
        "SourceIdentityReason", "SourceVersionState", "SourceVersionReason", "SourceETag", "SourceMajorVersion",
        "SourceMinorVersion", "SourceObservedAtUtc", "SourceArtifactReference", "SourceCapturedByteLength",
        "SourceExpectedByteLength", "SourceRawDigestAlgorithm", "SourceRawDigest", "SourceRawDigestScope",
        "SourceEncoding", "SourceReadState", "SourceReadReason", "SourceCaptureState", "SourceDecodingState",
        "SourceDecodingReason", "SourceContentState", "SourceContentReason", "DeclaredInherits", "NormalizedInherits",
        "BaseType", "TypeSource", "BaseTypeReason", "FrameworkDefaultAssumption", "PageParseState", "PageParseReason",
        "PageParseReliable", "VerifiedInheritsAbsence", "ConfigurationKnowledgeState", "ConfigurationApplicability",
        "EffectivePagesPageBaseType", "PageBaseTypeProvenance", "ConfigurationReason", "ConfigurationEvidenceJson",
        "ObservedHandlerState", "ObservedHandlerReason",
    };

    [Fact]
    public async Task Native_csv_preserves_original_columns_and_exact_quoted_CP1_values_with_independent_expectations()
    {
        using var fixture = new PageSourcePersistenceFixture();
        const string raw = "  Synthetic.Custom, \"quoted\"\r\nType  ";
        const string provenance = "Synthetic, \"configuration\"\r\nprovenance";
        var config = new PageBaseTypeConfiguration(new[]
        {
            new PageBaseTypeConfigurationEvidence("EffectiveOverride", "  Synthetic.Configured  ", provenance,
                new("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
                    "33333333-3333-3333-3333-333333333333", "/sites/source/Forms/original.aspx"), "Synthetic scoped override"),
        });
        var scan = await fixture.SeedAsync(config);
        var row = fixture.Page();
        row.ErrorDetail = "Synthetic, \"original\"\r\nerror";
        row.AssessmentStatus = "Complete";
        var physical = row.DiscoveryObservation.Identity with { Name = "source, \"quoted\"\r\nname.aspx" };
        var source = "<%@ Page Inherits='" + raw + "' %>";
        var bytes = new UnicodeEncoding(false, true, true).GetPreamble().Concat(Encoding.Unicode.GetBytes(source)).ToArray();
        await fixture.AcquireAsync(scan, row, await PageSourcePersistenceFixture.Capture(row, bytes, physical));
        await fixture.ExportAsync();
        var report = Path.Combine(fixture.DirectoryPath, "report");
        var (headers, records) = PageSourcePersistenceFixture.ReadIndependentCsv(Path.Combine(report, "discovery.csv"));
        headers.Should().Equal(OriginalHeaders.Concat(AddedHeaders));
        headers.Should().NotContain("PageType", "the original runtime classification remains ignored in discovery.csv");
        records.Should().ContainSingle();
        var exported = records.Single();
        var expected = new Dictionary<string, string>
        {
            ["RecordKey"] = row.RecordKey, ["RowType"] = "Page", ["ScopeType"] = "File",
            ["ParentScopeKey"] = "scope:synthetic-folder", ["Url"] = "/sites/source/Forms/original.aspx",
            ["SiteCollectionId"] = "11111111-1111-1111-1111-111111111111",
            ["WebId"] = "22222222-2222-2222-2222-222222222222",
            ["FileUniqueId"] = "33333333-3333-3333-3333-333333333333",
            ["ListId"] = "44444444-4444-4444-4444-444444444444", ["ListItemId"] = "7",
            ["FileName"] = "original.aspx", ["HomePage"] = "False", ["LibraryHidden"] = "True",
            ["ObservationMethod"] = "SyntheticForms", ["DiscoveryStatus"] = "Discovered", ["AssessmentStatus"] = "Complete",
            ["ErrorDetail"] = "Synthetic, \"original\"\r\nerror", ["ScanId"] = fixture.ScanId.ToString(),
            ["SiteUrl"] = "https://example.com/sites/source", ["WebUrl"] = "/sites/source",
            ["SourceEvidenceState"] = "Collected", ["OriginalDiscoveryUrl"] = "/sites/source/Forms/original.aspx",
            ["OriginalDiscoveryFileName"] = "original.aspx", ["OriginalDiscoveryObservedAtUtc"] = "2026-02-03T04:05:06.1234567Z",
            ["SourceSiteCollectionId"] = "11111111-1111-1111-1111-111111111111",
            ["SourceWebId"] = "22222222-2222-2222-2222-222222222222",
            ["SourceFileUniqueId"] = "33333333-3333-3333-3333-333333333333",
            ["SourceListId"] = "44444444-4444-4444-4444-444444444444", ["SourceListItemId"] = "7",
            ["SourceUrl"] = "/sites/source/Forms/original.aspx", ["SourceFileName"] = "source, \"quoted\"\r\nname.aspx",
            ["SourceIdentityState"] = "Resolved", ["SourceIdentityReason"] = "SiteWebFileIdsObserved",
            ["SourceVersionState"] = "ObservedVersion", ["SourceVersionReason"] = "FileVersionMetadataObservedBeforeDownload",
            ["SourceETag"] = "\"synthetic, version\"", ["SourceMajorVersion"] = "3", ["SourceMinorVersion"] = "7",
            ["SourceObservedAtUtc"] = "2026-02-03T04:05:07.7654321Z",
            ["SourceCapturedByteLength"] = bytes.LongLength.ToString(), ["SourceExpectedByteLength"] = bytes.LongLength.ToString(),
            ["SourceRawDigestAlgorithm"] = "SHA256", ["SourceRawDigest"] = Convert.ToHexString(SHA256.HashData(bytes)),
            ["SourceRawDigestScope"] = "CompleteCapturedResponse", ["SourceEncoding"] = "utf-16le",
            ["SourceReadState"] = "Complete", ["SourceReadReason"] = "StreamEndedNormally", ["SourceCaptureState"] = "Complete",
            ["SourceDecodingState"] = "Reliable", ["SourceDecodingReason"] = "BomSelectedStrictDecoder",
            ["SourceContentState"] = "Source", ["SourceContentReason"] = "PhysicalDownloadWithServerSourcePreamble",
            ["DeclaredInherits"] = raw, ["NormalizedInherits"] = "Synthetic.Custom, \"quoted\"\r\nType",
            ["BaseType"] = "Synthetic.Custom, \"quoted\"\r\nType", ["TypeSource"] = "Declared",
            ["BaseTypeReason"] = "ExplicitPageInherits;NotRuntimeTypeProof", ["FrameworkDefaultAssumption"] = "False",
            ["PageParseState"] = "Declared", ["PageParseReason"] = "ExplicitPageInherits",
            ["PageParseReliable"] = "True", ["VerifiedInheritsAbsence"] = "False",
            ["ConfigurationKnowledgeState"] = "EffectiveOverride", ["ConfigurationApplicability"] = "Applicable",
            ["EffectivePagesPageBaseType"] = "  Synthetic.Configured  ", ["PageBaseTypeProvenance"] = provenance,
            ["ConfigurationReason"] = "SuppliedEffectivePagesPageBaseType;Synthetic scoped override",
            ["ObservedHandlerState"] = "ServerOnlyUnavailable",
            ["ObservedHandlerReason"] = "ServerDiagnosticsNotCollected;PhysicalSourceIsNotAnObservedRequestHandler",
        };
        foreach (var pair in expected) exported[pair.Key].Should().Be(pair.Value, pair.Key);
        // Independent JSON inspection of explicitly expected values, not a product DTO/serializer round trip.
        using var evidence = JsonDocument.Parse(exported["SourceEvidenceJson"]);
        evidence.RootElement.GetProperty("Version").GetInt32().Should().Be(1);
        var observation = evidence.RootElement.GetProperty("Reads")[0];
        observation.GetProperty("OriginalBytes").GetBytesFromBase64().Should().Equal(bytes);
        observation.GetProperty("Page").GetProperty("DeclaredInherits").GetString().Should().Be(raw);
        observation.GetProperty("Page").GetProperty("Directives")[0].GetProperty("RawText").GetString().Should().Be(source);
        observation.GetProperty("Discovery").GetProperty("DiscoveryRecord").GetProperty("Metadata")
            .GetProperty("original, \"metadata\"").GetString().Should().Be("raw\nvalue");
        observation.GetProperty("ObservationId").GetString().Should().Be(exported["SourceObservationId"]);
        exported["SourceArtifactReference"].Should().Be(
            $"assessment.db#cp1/{fixture.ScanId:D}/{Uri.EscapeDataString(row.RecordKey)}/{exported["SourceObservationId"]}");
        (await fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, row.RecordKey, exported["SourceObservationId"])).Should().Equal(bytes);
        using var configured = JsonDocument.Parse(exported["ConfigurationEvidenceJson"]);
        configured.RootElement[0].GetProperty("Provenance").GetString().Should().Be(provenance);
        configured.RootElement[0].GetProperty("FileScope").GetProperty("ServerRelativePath").GetString()
            .Should().Be("/sites/source/Forms/original.aspx");
        File.ReadAllText(Path.Combine(report, "discovery.csv")).Should().Contain(
            "\"  Synthetic.Custom, \"\"quoted\"\"\r\nType  \"", "raw CRLF and quote escaping must survive the actual file");
        Directory.GetFiles(report).Select(Path.GetFileName).Should().BeEquivalentTo(new[]
        {
            "discovery.csv", "workflows.csv", "classicextensibilities.csv", "classicinfopath.csv", "classiclists.csv",
            "classicpages.csv", "classicpagewebparts.csv", "classicwebpartunique.csv", "classicusercustomactions.csv",
            "classicsitesummaries.csv", "classicwebsummaries.csv", "classicpublishingsitesummaries.csv",
        });
    }

    [Fact]
    public async Task Csv_separates_verified_null_explicit_empty_empty_response_unavailable_and_uncollected()
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        var fixtures = new[]
        {
            ("absent.aspx", "<%@ Page %>", "VerifiedAbsent", "FrameworkDefault", "Complete", "Source"),
            ("empty-inherits.aspx", "<%@ Page Inherits='' %>", "EmptyInherits", "Unknown", "Complete", "Source"),
            ("zero-response.aspx", "", "EmptySource", "Unknown", "Complete", "Empty"),
            ("unavailable.aspx", (string)null, "SourceUnavailable", "Unknown", "Denied", "NotInspected"),
        };
        foreach (var (name, text, _, _, _, _) in fixtures)
        {
            var row = fixture.Page(name, Guid.NewGuid(), item: null);
            var read = text == null ? AspxSourceReadResult.Unavailable(row.DiscoveryObservation,
                AspxSourceTransportState.Denied, "Synthetic, \"denied\"\nreason", version: new(PageSourcePersistenceFixture.ReadTime)) :
                await PageSourcePersistenceFixture.Capture(row, Encoding.UTF8.GetBytes(text));
            await fixture.AcquireAsync(scan, row, read);
        }
        var uncollected = fixture.Page("uncollected.aspx", Guid.NewGuid());
        await fixture.Writer().WriteAsync(new[] { uncollected });
        var exported = await fixture.ExportAsync();
        exported.Should().HaveCount(5);
        foreach (var (name, text, parse, typeSource, transport, content) in fixtures)
        {
            var record = exported.Single(value => value["FileName"] == name);
            record["DeclaredInherits"].Should().Be("");
            record["TypeSource"].Should().Be(typeSource);
            record["PageParseState"].Should().Be(parse);
            record["SourceReadState"].Should().Be(transport);
            record["SourceContentState"].Should().Be(content);
            record["SourceCapturedByteLength"].Should().Be(text == null ? "" : Encoding.UTF8.GetByteCount(text).ToString());
            record["SourceRawDigest"].Should().Be(text == null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
            record["BaseType"].Should().Be(name == "absent.aspx" ? "System.Web.UI.Page" : "");
            record["VerifiedInheritsAbsence"].Should().Be(name == "absent.aspx" ? "True" : "False");
            record["FrameworkDefaultAssumption"].Should().Be(name == "absent.aspx" ? "True" : "False");
            record["PublishingLayoutFamily"].Should().Be("Unknown");
            record["ConfigurationKnowledgeState"].Should().Be("Unknown");
            record["ObservedHandlerState"].Should().Be("ServerOnlyUnavailable");
            using var json = JsonDocument.Parse(record["SourceEvidenceJson"]);
            var declaration = json.RootElement.GetProperty("Reads")[0].GetProperty("Page").GetProperty("DeclaredInherits");
            if (name == "empty-inherits.aspx") declaration.GetString().Should().Be("");
            else declaration.ValueKind.Should().Be(JsonValueKind.Null);
            if (name == "unavailable.aspx")
            {
                record["SourceReadReason"].Should().Be("Synthetic, \"denied\"\nreason");
                record["SourceArtifactReference"].Should().Be("");
                record["SourceRawDigestAlgorithm"].Should().Be("");
                record["SourceVersionState"].Should().Be("ObservationTimeOnly");
            }
            if (name == "zero-response.aspx")
                record["SourceRawDigest"].Should().Be("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");
        }
        var notAttempted = exported.Single(value => value["FileName"] == "uncollected.aspx");
        notAttempted["SourceEvidenceState"].Should().Be("DiscoveryOnly", "discovery is collected, but source is still uncollected");
        notAttempted["SourceReadState"].Should().Be("");
        notAttempted["PageParseState"].Should().Be("");
        notAttempted["TypeSource"].Should().Be("");
        using var discoveryOnly = JsonDocument.Parse(notAttempted["SourceEvidenceJson"]);
        discoveryOnly.RootElement.GetProperty("Reads").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Independent_reader_preserves_CRLF_commas_escaped_quotes_and_observed_empty_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), "synthetic-independent-csv-" + Guid.NewGuid() + ".csv");
        File.WriteAllText(path, "A,B,C\r\n\"quoted, \"\"value\"\"\r\nnext\",,\"\"\r\n");
        try
        {
            var (headers, rows) = PageSourcePersistenceFixture.ReadIndependentCsv(path);
            headers.Should().Equal("A", "B", "C");
            rows.Should().ContainSingle();
            rows[0]["A"].Should().Be("quoted, \"value\"\r\nnext");
            rows[0]["B"].Should().Be("");
            rows[0]["C"].Should().Be("");
        }
        finally { File.Delete(path); }
    }
}
