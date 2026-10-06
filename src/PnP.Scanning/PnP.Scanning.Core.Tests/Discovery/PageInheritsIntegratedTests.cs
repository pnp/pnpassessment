using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using PnP.Scanning.Core.Tests.Storage;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Input = PnP.Scanning.Core.Tests.Fixtures.PageInheritsIntegrationFixture.Input;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class PageInheritsIntegratedTests
{
    private readonly ITestOutputHelper output;
    public PageInheritsIntegratedTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Sdk_download_to_native_reopen_and_independent_CSV_preserves_identity_encoding_and_declared_default_precedence()
    {
        const string raw = "  Synthetic.Custom, \"quoted\"\r\nType  ";
        const string source = "<%@ Page Inherits='" + raw + "' %>\n" +
            "<%@ Register Src='/sites/outside/not-acquired.aspx' TagPrefix='s' TagName='outside' %>\n" +
            "<html><a href='https://outside.example/never.aspx'>Synthetic link</a></html>";
        const string absent = "<%@ Page Language='C#' %>";
        const string root = "Microsoft.SharePoint.Publishing.PublishingLayoutPage, Microsoft.SharePoint.Publishing, " +
            "Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        var inputs = new[]
        {
            Make(1, "system-noitem.aspx", "_catalogs/masterpage", source),
            Make(2, "system-item.aspx", "_catalogs/masterpage", source, item: 7) with
                { Bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(source)).ToArray() },
            Make(3, "form-noitem.aspx", "Forms", absent),
            Make(4, "form-item.aspx", "Forms", absent, item: 7) with { Bytes = Encoded(absent, false) },
            Make(5, "view-noitem.aspx", "Views", absent) with { Bytes = Encoded(absent, true) },
            Make(6, "view-item.aspx", "Views", "<%@ pAgE\n language=\"C#\" InHeRiTs='Synthetic.View' %>", item: 7),
            Make(7, "qualified.aspx", "Pages", "<%@ Page Inherits='" + root + "' %>"),
            Make(8, "dynamic.aspx", "Pages", "<%@ Page CodeFile='synthetic.cs' Inherits='Synthetic.Dynamic' %>", item: 7),
            Make(9, "conflict.aspx", "Pages", absent),
            Make(10, "unsupported.aspx", "Pages", absent),
            Make(11, "known-no-override.aspx", "Pages", absent),
            Make(12, "time-only.aspx", "Pages", absent) with { ReturnVersionAndLength = false },
        };
        const string provenance = "Synthetic, \"configuration\"\r\nprovenance";
        var configuration = new PageBaseTypeConfiguration(new[]
        {
            Configuration(inputs[0], "EffectiveOverride", "Synthetic.Ignored", provenance),
            Configuration(inputs[2], "EffectiveOverride", "  Synthetic.Configured  ", provenance),
            Configuration(inputs[5], "EffectiveOverride", "Synthetic.Ignored", provenance),
            Configuration(inputs[8], "EffectiveOverride", "Synthetic.A", provenance),
            Configuration(inputs[8], "EffectiveOverride", "Synthetic.B", provenance),
            Configuration(inputs[9], "Unsupported", null, provenance),
            Configuration(inputs[10], "VerifiedNoOverride", null, provenance),
        });
        // Hand-listed results, not projections computed by the implementation under test.
        var expected = new Dictionary<string, (string Declaration, string Base, string Type, string Parse, string Knowledge, string Applicability, string Encoding)>
        {
            ["system-noitem.aspx"] = (raw, "Synthetic.Custom, \"quoted\"\r\nType", "Declared", "Declared", "EffectiveOverride", "Applicable", "utf-8"),
            ["system-item.aspx"] = (raw, "Synthetic.Custom, \"quoted\"\r\nType", "Declared", "Declared", "Unknown", "OutOfScope", "utf-8"),
            ["form-noitem.aspx"] = (null, "Synthetic.Configured", "ConfiguredDefault", "VerifiedAbsent", "EffectiveOverride", "Applicable", "utf-8"),
            ["form-item.aspx"] = (null, "System.Web.UI.Page", "FrameworkDefault", "VerifiedAbsent", "Unknown", "OutOfScope", "utf-16le"),
            ["view-noitem.aspx"] = (null, "System.Web.UI.Page", "FrameworkDefault", "VerifiedAbsent", "Unknown", "OutOfScope", "utf-16be"),
            ["view-item.aspx"] = ("Synthetic.View", "Synthetic.View", "Declared", "Declared", "EffectiveOverride", "Applicable", "utf-8"),
            ["qualified.aspx"] = (root, root, "Declared", "Declared", "Unknown", "OutOfScope", "utf-8"),
            ["dynamic.aspx"] = ("Synthetic.Dynamic", "Synthetic.Dynamic", "Declared", "Declared", "Unknown", "OutOfScope", "utf-8"),
            ["conflict.aspx"] = (null, null, "Unknown", "VerifiedAbsent", "Conflicting", "Applicable", "utf-8"),
            ["unsupported.aspx"] = (null, null, "Unknown", "VerifiedAbsent", "Unsupported", "Applicable", "utf-8"),
            ["known-no-override.aspx"] = (null, "System.Web.UI.Page", "FrameworkDefault", "VerifiedAbsent", "VerifiedNoOverride", "Applicable", "utf-8"),
            ["time-only.aspx"] = (null, "System.Web.UI.Page", "FrameworkDefault", "VerifiedAbsent", "Unknown", "OutOfScope", "utf-8"),
        };
        using var fixture = new PageInheritsIntegrationFixture();
        await fixture.InitializeAsync(configuration);
        await fixture.UseSdkAsync(inputs);
        var started = DateTimeOffset.UtcNow;
        // A non-ASPX input is discovered on the same surface but must not reach acquisition.
        var decoy = inputs[0].Record() with { SourceObjectId = "synthetic.css", FileName = "synthetic.css",
            PhysicalLocator = "/sites/discovery/synthetic.css", FileUniqueId = Guid.Empty.ToString("D") };
        await fixture.RunAsync(inputs.Select(value => value.Record()).Append(decoy), fixture.ReadWithSdkAsync);
        var finished = DateTimeOffset.UtcNow;
        var stored = await fixture.Writer.ReadPagesAsync(fixture.Database.ScanId, PageInheritsIntegrationFixture.Site, PageInheritsIntegrationFixture.Web);
        stored.Should().HaveCount(12);
        var csv = (await fixture.Database.ExportAsync()).Where(row => row["RowType"] == "Page").ToArray();
        csv.Should().HaveCount(12);
        var headers = PageSourcePersistenceFixture.ReadIndependentCsv(
            Path.Combine(fixture.Database.DirectoryPath, "report", "discovery.csv")).Headers;
        headers.Should().Equal(PageSourceCsvTests.OriginalHeaders.Concat(PageSourceCsvTests.AddedHeaders));
        foreach (var input in inputs)
        {
            using var assertions = new AssertionScope(input.Name);
            var row = stored.Single(value => value.FileUniqueId == input.FileId);
            var exported = csv.Single(value => value["FileUniqueId"] == input.FileId.ToString("D"));
            var result = expected[input.Name];
            row.DeclaredInherits.Should().Be(result.Declaration);
            row.BaseType.Should().Be(result.Base);
            row.TypeSource.Should().Be(result.Type);
            row.PageParseState.Should().Be(result.Parse);
            row.ConfigurationKnowledgeState.Should().Be(result.Knowledge);
            row.ConfigurationApplicability.Should().Be(result.Applicability);
            row.SourceEncoding.Should().Be(result.Encoding);
            row.SourceReadState.Should().Be("Complete");
            row.SourceCaptureState.Should().Be("Complete");
            row.SourceDecodingState.Should().Be("Reliable");
            row.SourceContentState.Should().Be("Source");
            row.PageParseReliable.Should().BeTrue();
            row.VerifiedInheritsAbsence.Should().Be(result.Parse == "VerifiedAbsent");
            row.FrameworkDefaultAssumption.Should().Be(result.Type == "FrameworkDefault");
            row.OriginalDiscoveryUrl.Should().Be(input.Path);
            row.OriginalDiscoveryFileName.Should().Be(input.Name);
            row.SourceUrl.Should().Be(input.Path);
            row.SourceFileName.Should().Be(input.Name);
            row.SiteCollectionId.Should().Be(PageInheritsIntegrationFixture.SiteId);
            row.WebId.Should().Be(PageInheritsIntegrationFixture.WebId);
            row.SourceFileUniqueId.Should().Be(input.FileId);
            row.SourceSiteCollectionId.Should().Be(PageInheritsIntegrationFixture.SiteId);
            row.SourceWebId.Should().Be(PageInheritsIntegrationFixture.WebId);
            row.ListItemId.Should().Be(input.Item);
            row.SourceListItemId.Should().Be(input.Item);
            row.SourceListId.Should().Be(input.Item.HasValue ? PageInheritsIntegrationFixture.ListId : null);
            row.SourceIdentityState.Should().Be("Resolved");
            row.SourceIdentityReason.Should().Be("SiteWebFileIdsObserved");
            row.SourceVersionState.Should().Be(input.ReturnVersionAndLength ? "ObservedVersion" : "ObservationTimeOnly");
            row.SourceETag.Should().Be(input.ReturnVersionAndLength ? "\"synthetic, version\"" : null);
            row.SourceMajorVersion.Should().Be(input.ReturnVersionAndLength ? 3 : null);
            row.SourceMinorVersion.Should().Be(input.ReturnVersionAndLength ? 7 : null);
            row.SourceExpectedByteLength.Should().Be(input.ReturnVersionAndLength ? input.Bytes.LongLength : null);
            if (!input.ReturnVersionAndLength) row.SourceVersionReason.Should().Be("VersionMetadataNotReturned;SourceObservationUtcRetained");
            row.SourceObservedAtUtc.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$");
            var observed = DateTimeOffset.ParseExact(row.SourceObservedAtUtc, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
            observed.Should().BeOnOrAfter(started).And.BeOnOrBefore(finished);
            foreach (var pair in new Dictionary<string, string>
            {
                ["DeclaredInherits"] = result.Declaration ?? "", ["NormalizedInherits"] = result.Declaration?.Trim() ?? "",
                ["BaseType"] = result.Base ?? "", ["TypeSource"] = result.Type, ["PageParseState"] = result.Parse,
                ["ConfigurationKnowledgeState"] = result.Knowledge, ["ConfigurationApplicability"] = result.Applicability,
                ["SourceEncoding"] = result.Encoding, ["SourceReadReason"] = "StreamEndedNormally",
                ["SourceContentReason"] = "PhysicalDownloadWithServerSourcePreamble",
                ["SourceIdentityReason"] = "SiteWebFileIdsObserved", ["SourceListItemId"] = input.Item?.ToString() ?? "",
                ["SourceETag"] = input.ReturnVersionAndLength ? "\"synthetic, version\"" : "",
                ["SourceMajorVersion"] = input.ReturnVersionAndLength ? "3" : "",
                ["SourceMinorVersion"] = input.ReturnVersionAndLength ? "7" : "",
                ["SourceObservedAtUtc"] = row.SourceObservedAtUtc,
                ["OriginalDiscoveryUrl"] = input.Path, ["OriginalDiscoveryFileName"] = input.Name,
                ["PageParseReliable"] = "True", ["VerifiedInheritsAbsence"] = result.Parse == "VerifiedAbsent" ? "True" : "False",
                ["FrameworkDefaultAssumption"] = result.Type == "FrameworkDefault" ? "True" : "False",
            }) exported[pair.Key].Should().Be(pair.Value, pair.Key);
            await VerifyBytesAndIndependentJsonAsync(fixture, row, exported, input.Bytes, result.Declaration);
            if (result.Parse == "VerifiedAbsent")
            {
                row.PublishingLayoutFamily.Should().Be("Unknown");
                row.PageTypeReason.Should().Contain("InheritsMissing");
            }
            if (result.Type == "FrameworkDefault")
            {
                row.BaseTypeReason.Should().Contain("FrameworkDefaultAssumption");
                if (result.Knowledge == "Unknown") row.BaseTypeReason.Should().NotContain("SuppliedVerifiedNoOverride");
            }
            if (input.Name == "qualified.aspx")
            {
                row.PublishingLayoutFamily.Should().Be("Member");
                row.AssessmentStatus.Should().Be("ExcludedAsset");
            }
            else row.AssessmentStatus.Should().BeNull();
            if (input.Name == "dynamic.aspx") row.PageTypeReason.Should().Contain("DynamicCompilationUnsupported");
            if (input.Name == "conflict.aspx") row.BaseTypeReason.Should().Contain("DefaultSuppressed:OverlappingEffectiveConfigurationEvidence");
            if (input.Name == "unsupported.aspx") row.BaseTypeReason.Should().Contain("DefaultSuppressed:SuppliedConfigurationUnsupported");
            if (result.Knowledge == "EffectiveOverride")
            {
                row.PageBaseTypeProvenance.Should().Be(provenance);
                exported["PageBaseTypeProvenance"].Should().Be(provenance);
                using var evidence = JsonDocument.Parse(exported["ConfigurationEvidenceJson"]);
                evidence.RootElement.EnumerateArray().Should().Contain(value =>
                    value.GetProperty("FileScope").GetProperty("FileUniqueId").GetString() == input.FileId.ToString("D") &&
                    value.GetProperty("FileScope").GetProperty("ServerRelativePath").GetString() == input.Path);
            }
            output.WriteLine($"Verified native reopen/independent CSV case: {input.Name}; {result.Type}; {result.Parse}; {result.Encoding}");
        }
        var first = stored.Single(row => row.FileName == "system-noitem.aspx").ReadSourceEvidence().Reads.Single();
        var bom = stored.Single(row => row.FileName == "system-item.aspx").ReadSourceEvidence().Reads.Single();
        first.RawDigest.Should().NotBe(bom.RawDigest, "BOM bytes are part of the raw digest");
        first.LegacySourceHash.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        bom.LegacySourceHash.Should().Be(first.LegacySourceHash, "legacy SourceHash intentionally hashes decoded text");
        first.LegacySourceHashKind.Should().Be("DecodedTextUtf8Sha256");
        fixture.AcquisitionCalls.Should().BeEquivalentTo(inputs.Select(value => value.Name), options => options.WithoutStrictOrdering());
        var requests = fixture.Http.Transport.Requests.Skip(fixture.BootstrapRequestCount).ToArray();
        requests.Should().HaveCount(24, "each admitted file has one metadata read and one physical download");
        requests.Count(value => value.Uri.AbsolutePath.EndsWith("/download.aspx")).Should().Be(12);
        requests.Should().OnlyContain(value => value.Uri.Host == "example.com" &&
            (value.Uri.AbsoluteUri.Contains("getfileby", StringComparison.OrdinalIgnoreCase) || value.Uri.AbsolutePath.EndsWith("/download.aspx")));
        requests.Should().NotContain(value => value.Uri.AbsoluteUri.Contains("ListItemAllFields", StringComparison.OrdinalIgnoreCase) ||
            value.Uri.AbsoluteUri.Contains("outside", StringComparison.OrdinalIgnoreCase));
        fixture.Http.AssertHeaders(true);
        using var db = fixture.Database.CreateContext();
        (await db.ClassicPages.CountAsync()).Should().Be(0, "source enrichment is not an additional scanner/body assessment");
    }

    [Fact]
    public async Task Acquisition_and_parse_failure_matrix_keeps_independent_evidence_and_never_invents_defaults_or_blocks_the_last_file()
    {
        // The inputs and expectations are intentionally listed independently of parser/DTO serialization.
        var cases = new[]
        {
            Failure(21, "http401", "<%@ Page Inherits='Synthetic.Candidate' %>", "Denied", "Complete", "Reliable", "Source", "Declared", "TransportHTTP401", "Synthetic.Candidate", http: 401),
            Failure(22, "http403", "<html>Access Denied</html>", "Denied", "Complete", "Reliable", "SemanticDenied", "PageDirectiveMissing", "TransportHTTP403", http: 403),
            Failure(23, "http500", "<%@ Page Inherits='Synthetic.Candidate' %>", "Failed", "Complete", "Reliable", "Source", "Declared", "TransportHTTP500", "Synthetic.Candidate", http: 500),
            Failure(24, "throw-denied", null, "Denied", "NotCaptured", "NotAttempted", "NotInspected", "SourceUnavailable", "Synthetic source denial"),
            Failure(25, "throw-failed", null, "Failed", "NotCaptured", "NotAttempted", "NotInspected", "SourceUnavailable", "Synthetic source failure"),
            Failure(26, "null-stream", null, "NotReturned", "NotCaptured", "NotAttempted", "NotInspected", "SourceUnavailable", "SourceStreamNotReturned"),
            Failure(27, "null-result", null, "NotReturned", "NotCaptured", "NotAttempted", "NotInspected", "SourceUnavailable", "ReadResultNotReturned"),
            Failure(28, "not-attempted", null, "NotAttempted", "NotCaptured", "NotAttempted", "NotInspected", "SourceUnavailable", "Synthetic source not attempted"),
            Failure(29, "login", "<html><form id=\"loginForm\">Sign in to your account</form></html>", "Complete", "Complete", "Reliable", "LoginShell", "PageDirectiveMissing", "StreamEndedNormally"),
            Failure(30, "semantic", "<!DOCTYPE html><html>Sorry, you don't have access</html>", "Complete", "Complete", "Reliable", "SemanticDenied", "PageDirectiveMissing", "StreamEndedNormally"),
            Failure(31, "rendered", "<html><body>Rendered content, not physical source</body></html>", "Complete", "Complete", "Reliable", "RenderedHtml", "PageDirectiveMissing", "StreamEndedNormally"),
            Failure(32, "unknown-payload", "Unrecognized response", "Complete", "Complete", "Reliable", "Unknown", "PageDirectiveMissing", "StreamEndedNormally"),
            Failure(33, "decode", null, "Complete", "Complete", "Unreliable", "Unknown", "SourceUnavailable", "StreamEndedNormally") with
                { Input = Make(33, "decode.aspx", "Pages", null) with { Bytes = new byte[] { 0xc3, 0x28 } } },
            Failure(34, "zero", "", "Complete", "Complete", "Reliable", "Empty", "EmptySource", "StreamEndedNormally"),
            Failure(35, "partial", "<%@ Page Inherits='Synthetic.Prefix' %>", "Partial", "Partial", "Reliable", "Source", "Declared", "ExpectedFileLengthMismatch", "Synthetic.Prefix"),
            Failure(36, "truncated", "<%@ Page Inherits='Synthetic.Prefix", "Partial", "Partial", "Reliable", "Source", "MalformedDirective", "ExpectedFileLengthMismatch", "Synthetic.Prefix"),
            Failure(37, "empty-inherits", "<%@ Page Inherits='' %>", "Complete", "Complete", "Reliable", "Source", "EmptyInherits", "StreamEndedNormally", ""),
            Failure(38, "equal-attributes", "<%@ Page Inherits='A' INHERITS='A' %>", "Complete", "Complete", "Reliable", "Source", "DuplicateAttribute", "StreamEndedNormally"),
            Failure(39, "conflicting-attributes", "<%@ Page Inherits='A' Inherits='B' %>", "Complete", "Complete", "Reliable", "Source", "DuplicateAttribute", "StreamEndedNormally"),
            Failure(40, "other-duplicate", "<%@ Page Language='C#' language='C#' Inherits='Synthetic.Candidate' %>", "Complete", "Complete", "Reliable", "Source", "DuplicateAttribute", "StreamEndedNormally", "Synthetic.Candidate"),
            Failure(41, "equal-pages", "<%@ Page Inherits='A' %><%@ Page Inherits='A' %>", "Complete", "Complete", "Reliable", "Source", "DuplicatePageDirective", "StreamEndedNormally"),
            Failure(42, "conflicting-pages", "<%@ Page Inherits='A' %><%@ Page Inherits='B' %>", "Complete", "Complete", "Reliable", "Source", "DuplicatePageDirective", "StreamEndedNormally"),
            Failure(43, "malformed", "<%@ Page Inherits='Synthetic.Candidate' Broken %>", "Complete", "Complete", "Reliable", "Source", "MalformedDirective", "StreamEndedNormally", "Synthetic.Candidate"),
            Failure(44, "master-register", "<%@ Master Inherits='Ignored.Master' %><%@ Register Inherits='Ignored.Register' %>", "Complete", "Complete", "Reliable", "Source", "PageDirectiveMissing", "StreamEndedNormally"),
            Failure(45, "misplaced", "<%@ Master %><html><%@ Page Inherits='Synthetic.Misplaced' %></html>", "Complete", "Complete", "Reliable", "Source", "MisplacedPageDirective", "StreamEndedNormally", "Synthetic.Misplaced"),
            Failure(46, "last-unrelated", "<%-- <%@ Page Inherits='Ignored.Comment' %> --%><%@ Page %><script>const s = \"<%@ Page Inherits='Ignored.Body' %>\";</script><!-- <%@ Page Inherits='Ignored.Html' %> -->", "Complete", "Complete", "Reliable", "Source", "VerifiedAbsent", "StreamEndedNormally"),
        };
        using var fixture = new PageInheritsIntegrationFixture();
        await fixture.InitializeAsync(new PageBaseTypeConfiguration(cases.Select(value =>
            Configuration(value.Input, "EffectiveOverride", "Synthetic.Configured", "Synthetic frozen override"))));
        await fixture.RunAsync(cases.Select(value => value.Input.Record()), async (row, token) =>
        {
            var test = cases.Single(value => value.Input.Name == row.FileName);
            switch (test.Input.Name)
            {
                case "throw-denied.aspx": throw new UnauthorizedAccessException("Synthetic source denial");
                case "throw-failed.aspx": throw new IOException("Synthetic source failure");
                case "null-result.aspx": return null;
                case "not-attempted.aspx": return AspxSourceReadResult.Unavailable(row.DiscoveryObservation,
                    AspxSourceTransportState.NotAttempted, "Synthetic source not attempted");
            }
            var expectedLength = test.Input.Bytes?.LongLength;
            if (test.Read == "Partial") expectedLength += 3;
            return await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
                new(PageSourcePersistenceFixture.ReadTime, "\"synthetic, version\"", 3, 7),
                _ => Task.FromResult<Stream>(test.Input.Bytes == null ? null : new MemoryStream(test.Input.Bytes)),
                token, expectedLength, test.Http);
        });
        var csv = (await fixture.Database.ExportAsync()).Where(value => value["RowType"] == "Page").ToArray();
        csv.Should().HaveCount(26);
        foreach (var test in cases)
        {
            using var assertions = new AssertionScope(test.Input.Name);
            var row = await fixture.Database.ReopenAsync(fixture.AcquiredRows.Single(value => value.FileName == test.Input.Name).RecordKey);
            var exported = csv.Single(value => value["FileName"] == test.Input.Name);
            row.SourceReadState.Should().Be(test.Read);
            row.SourceReadReason.Should().Contain(test.Reason);
            row.SourceCaptureState.Should().Be(test.Capture);
            row.SourceDecodingState.Should().Be(test.Decode);
            row.SourceContentState.Should().Be(test.Content);
            row.PageParseState.Should().Be(test.Parse);
            row.DeclaredInherits.Should().Be(test.Declaration);
            row.ConfigurationKnowledgeState.Should().Be("EffectiveOverride", "even applicable configuration cannot repair acquisition/parse failure");
            row.ConfigurationApplicability.Should().Be("Applicable");
            var control = test.Input.Name == "last-unrelated.aspx";
            row.TypeSource.Should().Be(control ? "ConfiguredDefault" : "Unknown");
            row.BaseType.Should().Be(control ? "Synthetic.Configured" : null);
            row.VerifiedInheritsAbsence.Should().Be(control);
            row.PageParseReliable.Should().Be(control);
            row.FrameworkDefaultAssumption.Should().BeFalse();
            row.AssessmentStatus.Should().BeNull("no source failure creates a whole-page skip");
            row.DiscoveryStatus.Should().Be("Discovered");
            row.PublishingLayoutFamily.Should().Be("Unknown");
            foreach (var pair in new Dictionary<string, string>
            {
                ["SourceReadState"] = test.Read, ["SourceCaptureState"] = test.Capture, ["SourceDecodingState"] = test.Decode,
                ["SourceContentState"] = test.Content, ["PageParseState"] = test.Parse, ["DeclaredInherits"] = test.Declaration ?? "",
                ["TypeSource"] = control ? "ConfiguredDefault" : "Unknown", ["BaseType"] = control ? "Synthetic.Configured" : "",
                ["PageParseReliable"] = control ? "True" : "False", ["VerifiedInheritsAbsence"] = control ? "True" : "False",
            }) exported[pair.Key].Should().Be(pair.Value, pair.Key);
            exported["SourceReadReason"].Should().Contain(test.Reason);
            await VerifyBytesAndIndependentJsonAsync(fixture, row, exported, test.Input.Bytes, test.Declaration);
            using var json = JsonDocument.Parse(exported["SourceEvidenceJson"]);
            var read = json.RootElement.GetProperty("Reads")[0];
            if (test.Http.HasValue) read.GetProperty("HttpStatusCode").GetInt32().Should().Be(test.Http.Value);
            if (test.Capture == "Partial")
            {
                row.SourceRawDigestScope.Should().Be("PartialCapturedBytes");
                row.SourceExpectedByteLength.Should().Be(test.Input.Bytes.LongLength + 3);
                exported["SourceRawDigestScope"].Should().Be("PartialCapturedBytes");
            }
            if (test.Decode == "Unreliable") row.SourceDecodingReason.Should().Be("StrictDecoderRejectedBytes");
            if (test.Input.Name == "zero.aspx")
                row.SourceRawDigest.Should().Be("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");
            output.WriteLine($"Verified integrated failure/control case: {test.Input.Name}; {test.Read}/{test.Capture}/{test.Decode}/{test.Content}/{test.Parse}");
        }
        fixture.AcquisitionCalls.Should().Equal(cases.Select(value => value.Input.Name));
        fixture.Http.Transport.Requests.Should().BeEmpty("these supplied stream fixtures do not open a live transport");
    }

    [Fact]
    public async Task Sdk_rename_version_reobservation_replacement_and_later_failure_retain_every_original_acquisition()
    {
        using var fixture = new PageInheritsIntegrationFixture();
        await fixture.InitializeAsync();
        var original = Make(61, "original.aspx", "Pages", "<%@ Page Inherits='Synthetic.Original' %>", item: 7) with
            { ETag = "\"one\"", Major = 1, Minor = 0 };
        await fixture.UseSdkAsync(original);
        await fixture.RunAsync(new[] { original.Record() }, fixture.ReadWithSdkAsync);
        var key = fixture.AcquiredRows[0].RecordKey;
        var originalRow = await fixture.Database.ReopenAsync(key);
        var originalObservation = originalRow.SourceObservationId;
        var renamedSource = original with { SourcePath = "/sites/discovery/Pages/renamed.aspx", SourceName = "renamed.aspx",
            ETag = "\"two\"", Major = 2, Minor = 1 };
        await fixture.UseSdkAsync(renamedSource);
        await fixture.RunAsync(new[] { original.Record() }, fixture.ReadWithSdkAsync);
        var nextDiscovery = original with { Name = "renamed.aspx", Path = "/sites/discovery/Pages/renamed.aspx",
            Bytes = Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Revised' %>"), ETag = "\"three\"", Major = 3, Minor = 2 };
        await fixture.UseSdkAsync(nextDiscovery);
        await fixture.RunAsync(new[] { nextDiscovery.Record() }, fixture.ReadWithSdkAsync);
        var replacement = nextDiscovery with { FileId = Guid.Parse("30000000-0000-0000-0000-000000000062"),
            Bytes = Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Replacement' %>"), ETag = "\"replacement\"" };
        await fixture.UseSdkAsync(replacement);
        await fixture.RunAsync(new[] { replacement.Record() }, fixture.ReadWithSdkAsync);
        var wrongIdentity = nextDiscovery with { SourceId = Guid.Parse("30000000-0000-0000-0000-000000000063"), ETag = "\"wrong-file\"" };
        var downloadsBeforeFailure = fixture.Http.Transport.Requests.Count(value => value.Uri.AbsolutePath.EndsWith("/download.aspx"));
        await fixture.UseSdkAsync(wrongIdentity);
        await fixture.RunAsync(new[] { nextDiscovery.Record() }, fixture.ReadWithSdkAsync);
        fixture.Http.Transport.Requests.Count(value => value.Uri.AbsolutePath.EndsWith("/download.aspx")).Should().Be(downloadsBeforeFailure,
            "a changed physical File UniqueId must be rejected before download");
        await fixture.Writer.UpdateExistingAsync(new[]
        {
            new ClassicPageDiscovery { ScanId = fixture.Database.ScanId, RecordKey = key, RowType = "Page",
                DiscoveryStatus = "Discovered", AssessmentStatus = "Complete", PageType = "WebPartPage",
                ContentTypeId = "0x0101", EvidenceJson = """{"SyntheticWebPartProfile":"Unrelated.Type"}""" },
        });
        var reopened = await fixture.Database.ReopenAsync(key);
        reopened.OriginalDiscoveryFileName.Should().Be("original.aspx");
        reopened.OriginalDiscoveryUrl.Should().Be("/sites/discovery/Pages/original.aspx");
        reopened.FileUniqueId.Should().Be(original.FileId);
        reopened.Url.Should().Be("/sites/discovery/Pages/renamed.aspx");
        reopened.SourceFileUniqueId.Should().Be(wrongIdentity.SourceId);
        reopened.SourceReadState.Should().Be("Failed");
        reopened.SourceReadReason.Should().Be("SourceFileIdentityChanged");
        reopened.TypeSource.Should().Be("Unknown", "latest conveniences cannot blend an old successful type with a new failed acquisition");
        reopened.DeclaredInherits.Should().BeNull();
        reopened.BaseType.Should().BeNull();
        var history = reopened.ReadSourceEvidence().Reads;
        history.Should().HaveCount(4);
        history.Select(value => value.ETag).Should().Equal("\"one\"", "\"two\"", "\"three\"", "\"wrong-file\"");
        history.Select(value => value.MajorVersion).Should().Equal(1, 2, 3, 3);
        history.Select(value => value.MinorVersion).Should().Equal(0, 1, 2, 2);
        history[0].PhysicalIdentity.Url.Should().Be("/sites/discovery/Pages/original.aspx");
        history[1].PhysicalIdentity.Url.Should().Be("/sites/discovery/Pages/renamed.aspx");
        history[0].Page.DeclaredInherits.Should().Be("Synthetic.Original");
        history[1].Page.DeclaredInherits.Should().Be("Synthetic.Original");
        history[2].Page.DeclaredInherits.Should().Be("Synthetic.Revised");
        history[3].IdentityComparisonState.Should().Be("Changed");
        history[3].IdentityComparisonReason.Should().Be("DiscoveryFileIdentityChanged");
        (await fixture.Writer.ReadSourceArtifactAsync(fixture.Database.ScanId, key, originalObservation)).Should().Equal(original.Bytes);
        foreach (var read in history.Take(3))
            read.RawDigest.Should().Be(Convert.ToHexString(SHA256.HashData(
                await fixture.Writer.ReadSourceArtifactAsync(fixture.Database.ScanId, key, read.ObservationId))));
        var csv = (await fixture.Database.ExportAsync()).Where(value => value["RowType"] == "Page").ToArray();
        csv.Should().HaveCount(2);
        csv.Should().OnlyContain(value => value["Url"] == "/sites/discovery/Pages/renamed.aspx", "URL is not physical identity");
        var latest = csv.Single(value => value["FileUniqueId"] == original.FileId.ToString("D"));
        latest["SourceReadState"].Should().Be("Failed");
        latest["SourceReadReason"].Should().Be("SourceFileIdentityChanged");
        latest["TypeSource"].Should().Be("Unknown");
        latest["DeclaredInherits"].Should().Be("");
        latest["SourceRawDigest"].Should().Be("");
        using var independent = JsonDocument.Parse(latest["SourceEvidenceJson"]);
        var reads = independent.RootElement.GetProperty("Reads");
        reads.GetArrayLength().Should().Be(4);
        reads[0].GetProperty("OriginalBytes").GetBytesFromBase64().Should().Equal(original.Bytes);
        reads[2].GetProperty("Page").GetProperty("DeclaredInherits").GetString().Should().Be("Synthetic.Revised");
        reads[3].GetProperty("OriginalBytes").ValueKind.Should().Be(JsonValueKind.Null);
        csv.Single(value => value["FileUniqueId"] == replacement.FileId.ToString("D"))["DeclaredInherits"]
            .Should().Be("Synthetic.Replacement", "the unrelated replacement's success remains independent");
        output.WriteLine("Verified rename, versions 1/0 -> 2/1 -> 3/2, same-URL/different-ID rows, metadata-only update, retained original bytes and latest identity failure.");
    }

    [Fact]
    public async Task Scheduled_scope_missing_path_unreturned_identity_and_changed_identity_are_persisted_without_out_of_scope_downloads()
    {
        var otherWeb = Guid.Parse("90000000-0000-0000-0000-000000000001");
        var inputs = new[]
        {
            Make(71, "other-web.aspx", "Pages", "<%@ Page %>") with { DiscoveryWebId = otherWeb },
            Make(72, "path-unreturned.aspx", "Pages", "<%@ Page %>"),
            Make(73, "id-unreturned.aspx", "Pages", "<%@ Page %>") with { ReturnUniqueId = false },
            Make(74, "id-changed.aspx", "Pages", "<%@ Page %>") with
                { SourceId = Guid.Parse("90000000-0000-0000-0000-000000000002") },
            Make(75, "outside-path.aspx", "Pages", "<%@ Page %>") with { Path = "/sites/outside/outside-path.aspx" },
            Make(76, "prefix-alias.aspx", "Pages", "<%@ Page %>") with { Path = "/sites/discovery-other/prefix-alias.aspx" },
            Make(77, "traversal.aspx", "Pages", "<%@ Page %>") with { Path = "/sites/discovery/%2e%2e/outside/traversal.aspx" },
            Make(78, "source-outside.aspx", "Pages", "<%@ Page %>") with { SourcePath = "/sites/outside/source-outside.aspx" },
        };
        using var fixture = new PageInheritsIntegrationFixture();
        await fixture.InitializeAsync();
        await fixture.UseSdkAsync(inputs);
        await fixture.RunAsync(inputs.Select(value => value.Name == "path-unreturned.aspx"
            ? value.Record() with { PhysicalLocator = null } : value.Record()), fixture.ReadWithSdkAsync);
        var expected = new Dictionary<string, (string State, string Reason)>
        {
            ["other-web.aspx"] = ("NotAttempted", "DiscoveryIdentityOutsideScheduledSiteWeb"),
            ["path-unreturned.aspx"] = ("NotAttempted", "SourcePathNotReturned"),
            ["id-unreturned.aspx"] = ("NotReturned", "SourceFileUniqueIdNotReturned"),
            ["id-changed.aspx"] = ("Failed", "SourceFileIdentityChanged"),
            ["outside-path.aspx"] = ("NotAttempted", "DiscoveryPathOutsideScheduledWeb"),
            ["prefix-alias.aspx"] = ("NotAttempted", "DiscoveryPathOutsideScheduledWeb"),
            ["traversal.aspx"] = ("NotAttempted", "DiscoveryPathOutsideScheduledWeb"),
            ["source-outside.aspx"] = ("NotAttempted", "SourcePathOutsideScheduledWeb"),
        };
        var csv = (await fixture.Database.ExportAsync()).Where(value => value["RowType"] == "Page").ToArray();
        foreach (var input in inputs)
        {
            var row = await fixture.Database.ReopenAsync(fixture.AcquiredRows.Single(value => value.FileName == input.Name).RecordKey);
            row.FileUniqueId.Should().Be(input.FileId, "unresolved or changed source identity does not rewrite discovery identity");
            row.SourceReadState.Should().Be(expected[input.Name].State);
            row.SourceReadReason.Should().Be(expected[input.Name].Reason);
            row.TypeSource.Should().Be("Unknown");
            row.SourceCapturedByteLength.Should().BeNull();
            row.SourceRawDigest.Should().BeNull();
            await VerifyBytesAndIndependentJsonAsync(fixture, row, csv.Single(value => value["FileName"] == input.Name), null, null);
            if (!input.ReturnUniqueId)
            {
                row.SourceFileUniqueId.Should().BeNull();
                row.SourceIdentityState.Should().Be("Unresolved");
                row.SourceIdentityReason.Should().Be("FileUniqueIdNotReturned");
            }
        }
        var requests = fixture.Http.Transport.Requests.Skip(fixture.BootstrapRequestCount).ToArray();
        requests.Should().HaveCount(3);
        requests.Should().OnlyContain(value => value.Uri.AbsoluteUri.Contains("getfileby", StringComparison.OrdinalIgnoreCase));
        requests.Should().NotContain(value => value.Uri.AbsoluteUri.Contains("other-web", StringComparison.Ordinal) ||
            value.Uri.AbsoluteUri.Contains("path-unreturned", StringComparison.Ordinal) ||
            value.Uri.AbsoluteUri.Contains("outside-path", StringComparison.Ordinal) ||
            value.Uri.AbsoluteUri.Contains("prefix-alias", StringComparison.Ordinal) ||
            value.Uri.AbsoluteUri.Contains("traversal", StringComparison.Ordinal));
        output.WriteLine("Verified eight independent non-download outcomes and exact request ledger: three in-scope metadata requests, zero downloads.");
    }

    internal static Input Make(int number, string name, string folder, string text, int? item = null) =>
        new(name, "/sites/discovery/" + folder + "/" + name,
            Guid.Parse($"30000000-0000-0000-0000-{number:000000000000}"), text == null ? null : Encoding.UTF8.GetBytes(text), item);

    internal static PageBaseTypeConfigurationEvidence Configuration(Input input, string state, string value, string provenance) =>
        new(state, value, provenance, new(DiscoveryTransportFixture.SiteId, DiscoveryTransportFixture.WebId,
            input.FileId.ToString("D"), input.Path), "Synthetic exact-file scope");

    private static byte[] Encoded(string text, bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
    }

    private sealed record FailureCase(Input Input, string Read, string Capture, string Decode, string Content,
        string Parse, string Reason, string Declaration, int? Http);
    private static FailureCase Failure(int number, string name, string text, string read, string capture, string decode,
        string content, string parse, string reason, string declaration = null, int? http = null) =>
        new(Make(number, name + ".aspx", "Pages", text), read, capture, decode, content, parse, reason, declaration, http);

    internal static async Task VerifyBytesAndIndependentJsonAsync(PageInheritsIntegrationFixture fixture,
        ClassicPageDiscovery row, Dictionary<string, string> exported, byte[] bytes, string declaration)
    {
        row.SourceEvidenceState.Should().Be("Collected");
        row.ObservedHandlerState.Should().Be("ServerOnlyUnavailable");
        row.ObservedHandlerReason.Should().Be("ServerDiagnosticsNotCollected;PhysicalSourceIsNotAnObservedRequestHandler");
        exported["ObservedHandlerState"].Should().Be("ServerOnlyUnavailable");
        exported["ObservedHandlerReason"].Should().Be("ServerDiagnosticsNotCollected;PhysicalSourceIsNotAnObservedRequestHandler");
        var retrieved = await fixture.Writer.ReadSourceArtifactAsync(fixture.Database.ScanId, row.RecordKey, row.SourceObservationId);
        using var json = JsonDocument.Parse(exported["SourceEvidenceJson"]);
        json.RootElement.GetProperty("Version").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("Reads").GetArrayLength().Should().Be(1);
        var read = json.RootElement.GetProperty("Reads")[0];
        read.GetProperty("ObservationId").GetString().Should().Be(exported["SourceObservationId"]);
        var raw = read.GetProperty("Page").GetProperty("DeclaredInherits");
        if (declaration == null) raw.ValueKind.Should().Be(JsonValueKind.Null);
        else raw.GetString().Should().Be(declaration);
        read.GetProperty("ReadState").GetString().Should().Be(exported["SourceReadState"]);
        read.GetProperty("ReadReason").GetString().Should().Be(exported["SourceReadReason"]);
        read.GetProperty("Page").GetProperty("ParseState").GetString().Should().Be(exported["PageParseState"]);
        read.GetProperty("Page").GetProperty("Reason").GetString().Should().Be(exported["BaseTypeReason"]);
        if (bytes == null)
        {
            retrieved.Should().BeNull();
            read.GetProperty("OriginalBytes").ValueKind.Should().Be(JsonValueKind.Null);
            read.GetProperty("CapturedByteLength").ValueKind.Should().Be(JsonValueKind.Null);
            row.SourceCapturedByteLength.Should().BeNull();
            exported["SourceRawDigest"].Should().Be("");
            exported["SourceCapturedByteLength"].Should().Be("");
            exported["SourceArtifactReference"].Should().Be("");
        }
        else
        {
            retrieved.Should().Equal(bytes);
            read.GetProperty("OriginalBytes").GetBytesFromBase64().Should().Equal(bytes);
            read.GetProperty("CapturedByteLength").GetInt64().Should().Be(bytes.LongLength);
            var digest = Convert.ToHexString(SHA256.HashData(retrieved));
            row.SourceRawDigest.Should().Be(digest);
            row.SourceRawDigestAlgorithm.Should().Be("SHA256");
            exported["SourceRawDigest"].Should().Be(digest);
            exported["SourceRawDigestAlgorithm"].Should().Be("SHA256");
            exported["SourceCapturedByteLength"].Should().Be(bytes.LongLength.ToString(CultureInfo.InvariantCulture));
            exported["SourceArtifactReference"].Should().Be(
                $"assessment.db#cp1/{fixture.Database.ScanId:D}/{Uri.EscapeDataString(row.RecordKey)}/{row.SourceObservationId}");
        }
    }
}
