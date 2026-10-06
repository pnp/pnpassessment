using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class AspxSourceReaderTests
{
    internal static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid WebId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid FileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid ListId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly DateTimeOffset Observed = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Text = "<%@ Page Inherits=\"Synthetic.Type\" %>café";
    private const string TextHash = "88AF2B773EF53235E46183636A7FF7BAB1A5F1EDA3D0285A75E4DF8B42BF2B8A";

    // Fixed vectors were independently encoded/hashed, not produced by the acquisition code.
    public static IEnumerable<object[]> Encodings()
    {
        yield return new object[] { "utf-8", 0, "PCVAIFBhZ2UgSW5oZXJpdHM9IlN5bnRoZXRpYy5UeXBlIiAlPmNhZsOp", 42, TextHash };
        yield return new object[] { "utf-8", 3, "77u/PCVAIFBhZ2UgSW5oZXJpdHM9IlN5bnRoZXRpYy5UeXBlIiAlPmNhZsOp", 45,
            "4B0BC8BAD5A8460114872891E7D239D25546C1E214F5733DE46D60678FF3FBD8" };
        yield return new object[] { "utf-16le", 2, "//48ACUAQAAgAFAAYQBnAGUAIABJAG4AaABlAHIAaQB0AHMAPQAiAFMAeQBuAHQAaABlAHQAaQBjAC4AVAB5AHAAZQAiACAAJQA+AGMAYQBmAOkA", 84,
            "81774A03E6F406814680B604798C03490F102414AE222E002B188B400A8C9FAC" };
        yield return new object[] { "utf-16be", 2, "/v8APAAlAEAAIABQAGEAZwBlACAASQBuAGgAZQByAGkAdABzAD0AIgBTAHkAbgB0AGgAZQB0AGkAYwAuAFQAeQBwAGUAIgAgACUAPgBjAGEAZgDp", 84,
            "8ECF6607E1F8DCFAF3E2F639618A3A8798DD72654C9D1EBF7F7B1C0CE69CD3F6" };
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Strict_decoding_keeps_exact_original_bytes_including_boms(string encoding,
        int bomBytes, string base64, int length, string digest)
    {
        var bytes = Convert.FromBase64String(base64);
        var read = await Capture(bytes);
        read.OriginalBytes.Should().Equal(bytes);
        read.CapturedByteLength.Should().Be(length);
        read.RawDigestAlgorithm.Should().Be("SHA256");
        read.RawDigest.Should().Be(digest);
        Convert.ToHexString(SHA256.HashData(read.OriginalBytes)).Should().Be(digest);
        read.RawDigestScope.Should().Be("CompleteCapturedResponse");
        read.Decoding.State.Should().Be(AspxSourceDecodingState.Reliable);
        read.Decoding.EncodingName.Should().Be(encoding);
        read.Decoding.BomByteCount.Should().Be(bomBytes);
        read.DecodedText.Should().Be(Text);
        read.LegacySourceHash.Should().Be(TextHash);
        AspxSourceReadResult.LegacySourceHashKind.Should().Be("DecodedTextUtf8Sha256");
        read.IsReliableSource.Should().BeTrue();
    }

    [Fact]
    public async Task Equal_text_has_distinct_raw_digests_but_the_same_legacy_text_hash()
    {
        var reads = new List<AspxSourceReadResult>();
        foreach (var vector in Encodings()) reads.Add(await Capture(Convert.FromBase64String((string)vector[2])));
        reads.Select(read => read.RawDigest).Should().OnlyHaveUniqueItems();
        reads.Select(read => read.DecodedText).Distinct().Should().ContainSingle().Which.Should().Be(Text);
        reads.Select(read => read.LegacySourceHash).Distinct().Should().ContainSingle().Which.Should().Be(TextHash);
    }

    [Fact]
    public async Task Retained_bytes_and_discovery_metadata_cannot_be_changed_by_a_consumer()
    {
        var metadata = new Dictionary<string, string> { ["raw"] = "original" };
        var record = Record() with { Metadata = metadata };
        var row = Row(record);
        var bytes = Encoding.UTF8.GetBytes(Text);
        var read = await Capture(bytes, row);
        metadata["raw"] = "rewritten";
        row.Url = "/sites/synthetic/renamed.aspx";
        row.FileName = "renamed.aspx";
        bytes[0] = 0;
        var exposed = read.OriginalBytes;
        exposed[0] = 0;
        read.OriginalBytes[0].Should().Be((byte)'<');
        read.RawDigest.Should().Be(TextHash);
        read.Discovery.DiscoveryRecord.Metadata["raw"].Should().Be("original");
        read.Discovery.Identity.Url.Should().Be("/sites/synthetic/default.aspx");
        read.Discovery.Identity.Name.Should().Be("default.aspx");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Identity_does_not_require_a_list_item(bool hasItem)
    {
        var row = Row(Record() with { ListId = hasItem ? ListId : null, ListItemId = hasItem ? 7 : null });
        var result = await Capture(Encoding.UTF8.GetBytes(Text), row);
        result.PhysicalIdentity.State.Should().Be(AspxIdentityState.Resolved);
        result.PhysicalIdentity.SiteCollectionId.Should().Be(SiteId);
        result.PhysicalIdentity.WebId.Should().Be(WebId);
        result.PhysicalIdentity.FileUniqueId.Should().Be(FileId);
        result.PhysicalIdentity.Url.Should().Be("/sites/synthetic/default.aspx");
        result.PhysicalIdentity.Name.Should().Be("default.aspx");
        result.PhysicalIdentity.ListId.Should().Be(hasItem ? ListId : null);
        result.PhysicalIdentity.ListItemId.Should().Be(hasItem ? 7 : null);
        result.PhysicalIdentity.ItemIdentityState.Should().Be(hasItem ? "Observed" : "Unavailable");
        result.PhysicalIdentity.ItemIdentityReason.Should().NotBeNullOrWhiteSpace();
        result.IsReliableSource.Should().BeTrue();
    }

    [Fact]
    public async Task Rename_and_version_change_retain_the_original_discovery_and_each_source_observation()
    {
        var row = Row();
        var firstVersion = new AspxSourceVersion(Observed, "\"version-1\"", 1, 0);
        var secondVersion = new AspxSourceVersion(Observed.AddSeconds(1), "\"version-2\"", 2, 0);
        var original = row.DiscoveryObservation;
        var renamed = original.Identity with { Url = "/sites/synthetic/renamed.aspx", Name = "renamed.aspx" };
        var first = await Capture(Encoding.UTF8.GetBytes(Text), row, version: firstVersion);
        var second = await Capture(Encoding.UTF8.GetBytes(Text), row, renamed, secondVersion);
        first.PhysicalIdentity.SamePhysicalFile(second.PhysicalIdentity).Should().BeTrue();
        first.Version.ETag.Should().Be("\"version-1\"");
        second.Version.ETag.Should().Be("\"version-2\"");
        first.Version.State.Should().Be("ObservedVersion");
        second.Version.MajorVersion.Should().Be(2);
        second.PhysicalIdentity.Url.Should().Be("/sites/synthetic/renamed.aspx");
        second.Discovery.Should().BeSameAs(original);
        second.Discovery.DiscoveryRecord.PhysicalLocator.Should().Be("/sites/synthetic/default.aspx");
    }

    [Fact]
    public async Task Same_url_and_item_number_do_not_conflate_different_file_unique_ids()
    {
        var first = Row(Record() with { ListId = ListId, ListItemId = 7 });
        var second = Row(Record() with { FileUniqueId = "55555555-5555-5555-5555-555555555555", ListId = ListId, ListItemId = 7 });
        first.Url.Should().Be(second.Url);
        first.RecordKey.Should().NotBe(second.RecordKey);
        var one = await Capture(Encoding.UTF8.GetBytes(Text), first);
        var two = await Capture(Encoding.UTF8.GetBytes(Text), second);
        one.PhysicalIdentity.SamePhysicalFile(two.PhysicalIdentity).Should().BeFalse();
        one.Discovery.DiscoveryRecord.FileUniqueId.Should().Be(FileId.ToString("D"));
        two.Discovery.DiscoveryRecord.FileUniqueId.Should().Be("55555555-5555-5555-5555-555555555555");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task File_identity_includes_site_and_web_not_only_file_guid(bool changeSite)
    {
        var record = Record();
        var different = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var first = Row(record);
        var second = Row(changeSite ? record with { SiteCollectionId = different } : record with { WebId = different });
        first.RecordKey.Should().NotBe(second.RecordKey);
        var a = await Capture(Encoding.UTF8.GetBytes(Text), first);
        var b = await Capture(Encoding.UTF8.GetBytes(Text), second);
        a.PhysicalIdentity.SamePhysicalFile(b.PhysicalIdentity).Should().BeFalse();
        a.PhysicalIdentity.FileUniqueId.Should().Be(b.PhysicalIdentity.FileUniqueId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Unresolved_observations_keep_raw_identity_strings_and_are_not_url_identity(string rawId)
    {
        var one = Row(Record() with { FileUniqueId = rawId, SourceObjectId = "observation-one", SiteCollectionId = null });
        var two = Row(Record() with { FileUniqueId = rawId, SourceObjectId = "observation-two", SiteCollectionId = null });
        one.RecordKey.Should().NotBe(two.RecordKey);
        one.FileUniqueId.Should().BeNull();
        var read = await Capture(Encoding.UTF8.GetBytes(Text), one);
        read.Discovery.DiscoveryRecord.FileUniqueId.Should().Be(rawId);
        read.Discovery.Identity.State.Should().Be(AspxIdentityState.Unresolved);
        read.PhysicalIdentity.Reason.Should().Contain("FileUniqueIdNotReturned").And.Contain("SiteCollectionIdNotReturned");
        read.PhysicalIdentity.SamePhysicalFile(two.DiscoveryObservation.Identity).Should().BeFalse();
        read.OriginalBytes.Should().Equal(Encoding.UTF8.GetBytes(Text));
        read.IsReliableSource.Should().BeFalse("an unresolved URL observation cannot prove a physical identity");
    }

    [Fact]
    public async Task Missing_version_is_explicit_and_observation_time_is_utc()
    {
        var version = new AspxSourceVersion(new DateTimeOffset(2026, 9, 20, 14, 0, 0, TimeSpan.FromHours(2)));
        var read = await Capture(Encoding.UTF8.GetBytes(Text), version: version);
        read.Version.State.Should().Be("ObservationTimeOnly");
        read.Version.Reason.Should().Contain("VersionMetadataNotReturned").And.Contain("SourceObservationUtcRetained");
        read.Version.ETag.Should().BeNull();
        read.Version.MajorVersion.Should().BeNull();
        read.Version.ObservedAtUtc.Should().Be(Observed);
        read.Version.ObservedAtUtc.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("wK8=")] // Invalid UTF-8, not a replacement-decoded successful response.
    [InlineData("//48")] // UTF-16 LE BOM followed by an incomplete code unit.
    [InlineData("/v/YAA==")] // UTF-16 BE with an unpaired high surrogate.
    [InlineData("PAA=")] // A BOM-less UTF-16 code unit cannot be safely inferred as UTF-8 source.
    public async Task Unreliable_decoding_keeps_bytes_and_digest_without_substituting_replacement_text(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var read = await Capture(bytes);
        read.TransportState.Should().Be(AspxSourceTransportState.Complete);
        read.CaptureState.Should().Be(AspxSourceCaptureState.Complete);
        read.Decoding.State.Should().Be(AspxSourceDecodingState.Unreliable);
        read.Decoding.Reason.Should().NotBeNullOrWhiteSpace();
        read.DecodedText.Should().BeNull();
        read.LegacySourceHash.Should().BeNull();
        read.OriginalBytes.Should().Equal(bytes);
        read.RawDigest.Should().Be(Convert.ToHexString(SHA256.HashData(bytes)));
        read.IsReliableSource.Should().BeFalse();
    }

    [Fact]
    public async Task Observed_empty_response_is_distinct_from_missing_or_unattempted_capture()
    {
        var row = Row();
        var empty = await Capture(Array.Empty<byte>());
        var missing = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(null), default);
        var unattempted = AspxSourceReadResult.Unavailable(row.DiscoveryObservation,
            AspxSourceTransportState.NotAttempted, "SourcePathNotReturned");
        empty.TransportState.Should().Be(AspxSourceTransportState.Complete);
        empty.ContentState.Should().Be(AspxSourceContentState.Empty);
        empty.ContentReason.Should().Be("ObservedZeroByteResponse");
        empty.CapturedByteLength.Should().Be(0);
        empty.RawDigest.Should().Be("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");
        missing.TransportState.Should().Be(AspxSourceTransportState.NotReturned);
        unattempted.TransportState.Should().Be(AspxSourceTransportState.NotAttempted);
        foreach (var unavailable in new[] { missing, unattempted })
        {
            unavailable.OriginalBytes.Should().BeNull();
            unavailable.CapturedByteLength.Should().BeNull();
            unavailable.ExpectedByteLength.Should().BeNull();
            unavailable.RawDigest.Should().BeNull();
            unavailable.Decoding.State.Should().Be(AspxSourceDecodingState.NotAttempted);
        }
        new[] { empty, missing, unattempted }.Should().OnlyContain(read => !read.IsReliableSource);
    }

    [Fact]
    public async Task Unknown_expected_length_is_not_fabricated_as_zero()
    {
        var read = await Capture(Encoding.UTF8.GetBytes(Text));
        read.ExpectedByteLength.Should().BeNull();
        read.ExpectedLengthState.Should().Be("NotReturned");
        read.ExpectedLengthReason.Should().Be("ExpectedFileByteLengthNotReturned");
        read.CapturedByteLength.Should().Be(42);
        read.IsReliableSource.Should().BeTrue("actual EOF and capture length are observed independently");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Transport_denials_keep_returned_error_bodies_and_independent_decode_evidence(int code)
    {
        var bytes = Encoding.UTF8.GetBytes("<html>Access Denied</html>");
        var row = Row();
        var result = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(new MemoryStream(bytes)), default, httpStatusCode: code);
        result.TransportState.Should().Be(AspxSourceTransportState.Denied);
        result.HttpStatusCode.Should().Be(code);
        result.TransportReason.Should().Contain("TransportHTTP" + code);
        result.CaptureState.Should().Be(AspxSourceCaptureState.Complete);
        result.Decoding.State.Should().Be(AspxSourceDecodingState.Reliable);
        result.OriginalBytes.Should().Equal(bytes);
        result.ContentState.Should().Be(AspxSourceContentState.SemanticDenied);
        result.IsReliableSource.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_transport_exception_without_a_body_does_not_invent_empty_bytes(HttpStatusCode code)
    {
        var row = Row();
        var result = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromException<Stream>(new HttpRequestException("synthetic denial", null, code)), default);
        result.TransportState.Should().Be(AspxSourceTransportState.Denied);
        result.HttpStatusCode.Should().Be((int)code);
        result.CapturedByteLength.Should().BeNull();
        result.OriginalBytes.Should().BeNull();
        result.ContentState.Should().Be(AspxSourceContentState.NotInspected);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_known_denial_without_a_returned_stream_retains_transport_denial(int code)
    {
        var row = Row();
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(null), default, httpStatusCode: code);
        read.TransportState.Should().Be(AspxSourceTransportState.Denied);
        read.TransportReason.Should().Contain("TransportHTTP" + code).And.Contain("SourceStreamNotReturned");
        read.CapturedByteLength.Should().BeNull();
        read.CaptureState.Should().Be(AspxSourceCaptureState.NotCaptured);
    }

    [Theory]
    [InlineData("<html><form id=\"loginForm\">Sign in to your account</form></html>", "LoginShell")]
    [InlineData("<!DOCTYPE html><html>Sorry, you don't have access</html>", "SemanticDenied")]
    [InlineData("{\"error\":{\"code\":\"-2147024891\",\"message\":\"Access denied\"}}", "SemanticDenied")]
    [InlineData("<html><body>Rendered content, not physical source</body></html>", "RenderedHtml")]
    [InlineData("Unrecognized response", "Unknown")]
    public async Task Successful_transport_does_not_make_login_denial_or_rendered_html_reliable_source(
        string text, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var read = await Capture(bytes);
        read.TransportState.Should().Be(AspxSourceTransportState.Complete);
        read.CapturedByteLength.Should().Be(bytes.Length);
        read.OriginalBytes.Should().Equal(bytes);
        read.ContentState.ToString().Should().Be(content);
        read.ContentReason.Should().NotBeNullOrWhiteSpace();
        read.IsReliableSource.Should().BeFalse();
    }

    [Fact]
    public async Task Utf16_login_shell_is_detected_after_strict_decoding_and_keeps_its_original_digest()
    {
        var text = "<html><form id=\"loginForm\">Sign in to your account</form></html>";
        var bytes = new byte[] { 0xfe, 0xff }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();
        var read = await Capture(bytes);
        read.Decoding.EncodingName.Should().Be("utf-16be");
        read.ContentState.Should().Be(AspxSourceContentState.LoginShell);
        read.OriginalBytes.Should().Equal(bytes);
        read.RawDigest.Should().NotBe(read.LegacySourceHash);
    }

    [Fact]
    public async Task Denial_examples_inside_physical_source_are_not_login_shells()
    {
        var source = "<%-- leading source comment --%>\n<%@ Page Language='C#' %><html>Access Denied; Sign in to your account</html>";
        var read = await Capture(Encoding.UTF8.GetBytes(source));
        read.ContentState.Should().Be(AspxSourceContentState.Source);
        read.IsReliableSource.Should().BeTrue();
    }

    [Fact]
    public async Task Short_stream_against_observed_length_has_an_explicit_partial_digest()
    {
        var bytes = Encoding.UTF8.GetBytes(Text);
        var row = Row();
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(new MemoryStream(bytes)), default, bytes.Length + 10);
        read.TransportState.Should().Be(AspxSourceTransportState.Partial);
        read.TransportReason.Should().Be("ExpectedFileLengthMismatch");
        read.CapturedByteLength.Should().Be(bytes.Length);
        read.ExpectedByteLength.Should().Be(bytes.Length + 10);
        read.RawDigest.Should().Be(TextHash);
        read.RawDigestScope.Should().Be("PartialCapturedBytes");
        read.Decoding.State.Should().Be(AspxSourceDecodingState.Reliable);
        read.IsReliableSource.Should().BeFalse();
    }

    [Fact]
    public async Task Stream_failure_keeps_exact_prefix_bytes_and_disposes_the_stream()
    {
        var row = Row();
        var prefix = Encoding.UTF8.GetBytes(Text);
        var stream = new InterruptingStream(prefix);
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(stream), default);
        read.TransportState.Should().Be(AspxSourceTransportState.Partial);
        read.TransportReason.Should().Contain("StreamReadFailed").And.Contain("synthetic truncation");
        read.CaptureState.Should().Be(AspxSourceCaptureState.Partial);
        read.OriginalBytes.Should().Equal(prefix);
        read.RawDigest.Should().Be(TextHash);
        read.RawDigestScope.Should().Be("PartialCapturedBytes");
        read.ExpectedByteLength.Should().BeNull();
        stream.Disposed.Should().BeTrue();
        read.IsReliableSource.Should().BeFalse();
    }

    [Fact]
    public async Task Open_failure_is_not_a_read_of_zero_bytes()
    {
        var row = Row();
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromException<Stream>(new IOException("synthetic open failure")), default);
        read.TransportState.Should().Be(AspxSourceTransportState.Failed);
        read.CaptureState.Should().Be(AspxSourceCaptureState.NotCaptured);
        read.OriginalBytes.Should().BeNull();
        read.CapturedByteLength.Should().BeNull();
        read.TransportReason.Should().Contain("synthetic open failure");
    }

    [Fact]
    public async Task Stream_failure_before_any_byte_is_not_an_observed_empty_response()
    {
        var row = Row();
        var stream = new InterruptingStream(Array.Empty<byte>(), throwOnFirstRead: true);
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(stream), default);
        read.CapturedByteLength.Should().Be(0, "zero captured bytes is known, but response length is not");
        read.ExpectedByteLength.Should().BeNull();
        read.TransportState.Should().Be(AspxSourceTransportState.Partial);
        read.CaptureState.Should().Be(AspxSourceCaptureState.Partial);
        read.ContentState.Should().Be(AspxSourceContentState.NotInspected);
        read.ContentReason.Should().Contain("EndOfStreamNotObserved");
        read.RawDigestScope.Should().Be("PartialCapturedBytes");
        read.IsReliableSource.Should().BeFalse();
    }

    [Fact]
    public async Task A_capture_limit_does_not_claim_end_of_stream_or_a_whole_file_digest()
    {
        var row = Row();
        var bytes = Encoding.UTF8.GetBytes(Text);
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(new MemoryStream(bytes)), default, maximumCaptureBytes: 12);
        read.OriginalBytes.Should().Equal(bytes.Take(12));
        read.CapturedByteLength.Should().Be(12);
        read.TransportState.Should().Be(AspxSourceTransportState.Partial);
        read.RawDigestScope.Should().Be("PartialCapturedBytes");
        read.TransportReason.Should().Contain("EndOfStreamNotObserved");
        read.IsReliableSource.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_before_open_prevents_work()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var calls = 0;
        var row = Row();
        var operation = () => AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => { calls++; return Task.FromResult<Stream>(new MemoryStream()); }, cancel.Token);
        await operation.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_during_read_is_not_a_success_or_a_failed_observation()
    {
        var row = Row();
        var stream = new InterruptingStream(Encoding.UTF8.GetBytes(Text), cancellation: true);
        var operation = () => AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(stream), default);
        await operation.Should().ThrowAsync<OperationCanceledException>();
        stream.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Adapter_retains_success_and_failure_facets_without_promoting_partial_source()
    {
        var row = Row();
        var scan = new Scan { PublishingLayoutRuleVersion = PublishingLayoutTypeCatalog.CurrentRuleVersion };
        var source = Encoding.UTF8.GetBytes(PublishingLayoutTypeEvidenceTests.Source(PublishingLayoutTypeEvidenceTests.Root));
        var success = await Capture(source, row);
        await AspxSourceAcquisition.ForScan(scan, (_, _) => Task.FromResult(success))(row, default);
        PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeTrue();
        var originalHash = success.RawDigest;
        await AspxSourceAcquisition.ForScan(scan, (_, _) => throw new IOException("synthetic second facet failure"))(row, default);
        row.SourceReads.Should().HaveCount(2);
        row.SourceReads[0].RawDigest.Should().Be(originalHash);
        row.SourceReads[0].OriginalBytes.Should().Equal(source);
        row.SourceReads[1].TransportState.Should().Be(AspxSourceTransportState.Failed);
        var observations = JsonSerializer.Deserialize<PublishingLayoutTypeEvidence.Observation[]>(row.PageTypeEvidenceJson);
        observations.Should().Contain(value => value.Decision == "Member" && value.SourceHash == success.LegacySourceHash);
        observations.Should().Contain(value => value.SourceStatus == "Failed");
        observations.Should().OnlyContain(value => value.SourceHashKind == "DecodedTextUtf8Sha256");
        row.AssessmentStatus.Should().BeNull();
        row.DiscoveryStatus.Should().Be("Discovered");
        PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeFalse("a second failure must remain visible");
    }

    [Fact]
    public async Task A_complete_directive_in_a_truncated_capture_cannot_prove_family_membership()
    {
        var row = Row();
        var source = Encoding.UTF8.GetBytes(PublishingLayoutTypeEvidenceTests.Source(PublishingLayoutTypeEvidenceTests.Root));
        var partial = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(Observed), _ => Task.FromResult<Stream>(new InterruptingStream(source)), default);
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 }, (_, _) => Task.FromResult(partial))(row, default);
        row.SourceReads.Should().ContainSingle().Which.DecodedText.Should().Contain("PublishingLayoutPage");
        row.PageTypeSourceStatus.Should().Be("Unknown");
        row.PublishingLayoutFamily.Should().Be("Unknown");
        row.PageTypeReason.Should().Contain("StreamReadFailed");
        row.DeclaredPageType.Should().BeNull();
        PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeFalse();
    }

    [Fact]
    public async Task Adapter_propagates_cancellation_even_without_a_cancelled_caller_token()
    {
        var row = Row();
        var callback = AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 },
            (_, _) => throw new OperationCanceledException("synthetic cancellation"));
        var operation = () => callback(row, default);
        await operation.Should().ThrowAsync<OperationCanceledException>();
        row.SourceReads.Should().BeEmpty();
        row.PageTypeEvidenceJson.Should().BeNull();
    }

    [Fact]
    public async Task Historical_family_policy_does_not_prevent_acquisition_or_invent_type_authority()
    {
        var row = Row();
        var read = await Capture(Encoding.UTF8.GetBytes(Text), row);
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 0 }, (_, _) => Task.FromResult(read))(row, default);
        row.SourceReads.Should().ContainSingle();
        row.PageTypeEvidenceJson.Should().BeNull();
        row.PageTypeEvidenceOrigin.Should().Be("None");
    }

    internal static RawDiscoveryRecord Record() => new("synthetic-source", FileId.ToString("D"), "synthetic-container",
        "default.aspx", "/sites/synthetic/default.aspx", true, "synthetic-permission",
        SiteCollectionId: SiteId, WebId: WebId, ObservationMethod: "SyntheticRawFiles");

    internal static ClassicPageDiscovery Row(RawDiscoveryRecord record = null) => AssessmentWebDiscovery.Page(
        Guid.Parse("66666666-6666-6666-6666-666666666666"), "https://example.com/sites/synthetic", "/sites/synthetic",
        new("synthetic-scope", null, DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles,
            "/sites/synthetic", "synthetic-permission"), record ?? Record(), 1);

    internal static Task<AspxSourceReadResult> Capture(byte[] bytes, ClassicPageDiscovery row = null,
        AspxFileIdentity identity = null, AspxSourceVersion version = null)
    {
        row ??= Row();
        return AspxSourceReader.ReadAsync(row.DiscoveryObservation, identity ?? row.DiscoveryObservation.Identity,
            version ?? new(Observed), _ => Task.FromResult<Stream>(new MemoryStream(bytes)), default);
    }

    internal sealed class InterruptingStream : MemoryStream
    {
        private readonly bool cancellation;
        private bool returned;
        internal bool Disposed { get; private set; }
        internal InterruptingStream(byte[] prefix, bool cancellation = false, bool throwOnFirstRead = false) : base(prefix)
        {
            this.cancellation = cancellation;
            returned = throwOnFirstRead;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (returned) return ValueTask.FromException<int>(cancellation
                ? new OperationCanceledException("synthetic cancellation") : new IOException("synthetic truncation"));
            returned = true;
            return base.ReadAsync(buffer, token);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
