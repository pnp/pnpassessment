using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class PageBaseTypeAcquisitionTests
{
    [Theory]
    [InlineData("NotAttempted")]
    [InlineData("NotReturned")]
    [InlineData("Denied")]
    [InlineData("Failed")]
    public void Unavailable_reads_are_not_empty_captures_or_verified_absence(string state)
    {
        var row = AspxSourceReaderTests.Row();
        var transport = Enum.Parse<AspxSourceTransportState>(state);
        var read = AspxSourceReadResult.Unavailable(row.DiscoveryObservation, transport, "Synthetic" + state);
        var result = PageBaseTypeProjection.Inspect(read, PageBaseTypeProjectionTests.ApplicableConfiguration());
        result.SourceRead.Should().BeSameAs(read);
        result.SourceRead.TransportState.ToString().Should().Be(state);
        result.SourceRead.TransportReason.Should().Be("Synthetic" + state);
        result.SourceRead.CaptureState.Should().Be(AspxSourceCaptureState.NotCaptured);
        result.SourceRead.CapturedByteLength.Should().BeNull();
        result.SourceRead.ExpectedByteLength.Should().BeNull();
        result.SourceRead.OriginalBytes.Should().BeNull();
        result.SourceRead.RawDigest.Should().BeNull();
        result.SourceRead.ContentState.Should().Be(AspxSourceContentState.NotInspected);
        result.SourceRead.Decoding.State.Should().Be(AspxSourceDecodingState.NotAttempted);
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.SourceUnavailable);
        result.Parse.IsReliableAbsence.Should().BeFalse();
        result.IsVerifiedAbsence.Should().BeFalse();
        result.IsReliableParse.Should().BeFalse();
        result.DeclaredInherits.Should().BeNull();
        result.BaseType.Should().BeNull();
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.Reason.Should().Contain("Synthetic" + state);
        var withoutConfiguration = PageBaseTypeProjection.Inspect(read);
        withoutConfiguration.TypeSource.Should().Be(PageTypeSource.Unknown);
        withoutConfiguration.BaseType.Should().BeNull("unavailable acquisition suppresses the framework default too");
        withoutConfiguration.IsVerifiedAbsence.Should().BeFalse();
    }

    [Fact]
    public async Task A_not_returned_result_is_distinct_from_an_observed_zero_byte_response()
    {
        var missing = PageBaseTypeProjection.Inspect(null);
        missing.SourceRead.Should().BeNull();
        missing.Parse.Status.Should().Be(PageDirectiveParseStatus.SourceUnavailable);
        missing.Reason.Should().Be("SourceReadResultNotReturned");
        missing.TypeSource.Should().Be(PageTypeSource.Unknown);
        missing.BaseType.Should().BeNull();
        var row = AspxSourceReaderTests.Row();
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 },
            (_, _) => Task.FromResult<AspxSourceReadResult>(null))(row, default);
        row.SourceReads.Single().TransportState.Should().Be(AspxSourceTransportState.NotReturned);
        row.SourceReads.Single().CapturedByteLength.Should().BeNull();
        row.SourceReads.Single().TransportReason.Should().Be("ReadResultNotReturned");
        row.PageBaseTypeProjections.Single().TypeSource.Should().Be(PageTypeSource.Unknown);
        row.PageBaseTypeProjections.Single().Parse.Status.Should().Be(PageDirectiveParseStatus.SourceUnavailable);
        var empty = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Array.Empty<byte>()));
        empty.SourceRead.TransportState.Should().Be(AspxSourceTransportState.Complete);
        empty.SourceRead.CaptureState.Should().Be(AspxSourceCaptureState.Complete);
        empty.SourceRead.ContentState.Should().Be(AspxSourceContentState.Empty);
        empty.SourceRead.CapturedByteLength.Should().Be(0, "this zero was observed, not invented for a missing response");
        empty.SourceRead.OriginalBytes.Should().BeEmpty();
        empty.Parse.Status.Should().Be(PageDirectiveParseStatus.EmptySource);
        empty.Parse.IsReliableAbsence.Should().BeFalse();
        empty.IsVerifiedAbsence.Should().BeFalse();
        empty.BaseType.Should().BeNull();
        empty.TypeSource.Should().Be(PageTypeSource.Unknown);
    }

    [Theory]
    [InlineData("<html><form id=\"loginForm\">Sign in to your account</form></html>", "LoginShell")]
    [InlineData("<!DOCTYPE html><html>Sorry, you don't have access</html>", "SemanticDenied")]
    [InlineData("<html><body>Rendered, not physical source</body></html>", "RenderedHtml")]
    [InlineData("<html><script>const example = \"<%@ Page Inherits='Synthetic.Fake' %>\";</script></html>", "RenderedHtml")]
    [InlineData("Synthetic unrecognized payload", "Unknown")]
    public async Task Login_rendered_denied_and_unknown_payloads_keep_independent_states_and_suppress_defaults(string text, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var read = await AspxSourceReaderTests.Capture(bytes);
        var result = PageBaseTypeProjection.Inspect(read, PageBaseTypeProjectionTests.ApplicableConfiguration());
        read.TransportState.Should().Be(AspxSourceTransportState.Complete, "a normal EOF does not mean a successful source inspection");
        read.CaptureState.Should().Be(AspxSourceCaptureState.Complete);
        read.ContentState.ToString().Should().Be(content);
        read.Decoding.State.Should().Be(AspxSourceDecodingState.Reliable);
        read.CapturedByteLength.Should().Be(bytes.Length);
        read.OriginalBytes.Should().Equal(bytes);
        read.ContentReason.Should().NotBeNullOrWhiteSpace();
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.PageDirectiveMissing);
        result.Parse.IsReliableAbsence.Should().BeFalse();
        result.IsReliableParse.Should().BeFalse();
        result.IsVerifiedAbsence.Should().BeFalse();
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.BaseType.Should().BeNull();
        result.DeclaredInherits.Should().BeNull();
        result.Reason.Should().Contain(content);
        var withoutConfiguration = PageBaseTypeProjection.Inspect(read);
        withoutConfiguration.TypeSource.Should().Be(PageTypeSource.Unknown);
        withoutConfiguration.BaseType.Should().BeNull();
        withoutConfiguration.SourceRead.ContentState.ToString().Should().Be(content);
    }

    [Theory]
    [InlineData(401, "Denied")]
    [InlineData(403, "Denied")]
    [InlineData(500, "Failed")]
    public async Task A_returned_declaration_in_a_denied_or_failed_response_is_only_retained_evidence(int code, string transport)
    {
        var row = AspxSourceReaderTests.Row();
        var text = PublishingLayoutTypeEvidenceTests.Source(PublishingLayoutTypeEvidenceTests.Root);
        var bytes = Encoding.UTF8.GetBytes(text);
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(AspxSourceReaderTests.Observed), _ => Task.FromResult<Stream>(new MemoryStream(bytes)), default, httpStatusCode: code);
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 },
            (_, _) => Task.FromResult(read), PageBaseTypeProjectionTests.ApplicableConfiguration())(row, default);
        var result = row.PageBaseTypeProjections.Single();
        result.SourceRead.TransportState.ToString().Should().Be(transport);
        result.SourceRead.HttpStatusCode.Should().Be(code);
        result.SourceRead.CaptureState.Should().Be(AspxSourceCaptureState.Complete);
        result.SourceRead.CapturedByteLength.Should().Be(bytes.Length);
        result.SourceRead.OriginalBytes.Should().Equal(bytes);
        result.SourceRead.ContentState.Should().Be(AspxSourceContentState.Source);
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.Declared, "the lexical state remains independent from acquisition authority");
        result.DeclaredInherits.Should().Be(PublishingLayoutTypeEvidenceTests.Root);
        result.IsReliableParse.Should().BeFalse();
        result.IsVerifiedAbsence.Should().BeFalse();
        result.BaseType.Should().BeNull();
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        row.PageTypeSourceStatus.Should().Be(transport);
        row.PublishingLayoutFamily.Should().Be("Unknown");
        row.DeclaredPageType.Should().BeNull("the inherited field must not promote fragments into verified family evidence");
        PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeFalse();
        var family = JsonSerializer.Deserialize<PublishingLayoutTypeEvidence.Observation[]>(row.PageTypeEvidenceJson).Single();
        family.DirectiveEvidence.Should().Be(text);
        family.ParseStatus.Should().Be("Declared");
        family.Declaration.Should().BeNull();
        PageBaseTypeProjection.Inspect(read).BaseType.Should().BeNull();
    }

    [Theory]
    [InlineData("ww==")]
    [InlineData("//4A")]
    public async Task Decode_errors_preserve_raw_bytes_and_are_not_null_or_empty_declarations(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var read = await AspxSourceReaderTests.Capture(bytes);
        var result = PageBaseTypeProjection.Inspect(read, PageBaseTypeProjectionTests.ApplicableConfiguration());
        read.TransportState.Should().Be(AspxSourceTransportState.Complete);
        read.CaptureState.Should().Be(AspxSourceCaptureState.Complete);
        read.Decoding.State.Should().Be(AspxSourceDecodingState.Unreliable);
        read.DecodedText.Should().BeNull();
        read.ContentState.Should().Be(AspxSourceContentState.Unknown);
        read.CapturedByteLength.Should().Be(bytes.Length);
        read.OriginalBytes.Should().Equal(bytes);
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.SourceUnavailable);
        result.DeclaredInherits.Should().BeNull();
        result.Parse.IsReliableAbsence.Should().BeFalse();
        result.IsVerifiedAbsence.Should().BeFalse();
        result.BaseType.Should().BeNull();
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.Reason.Should().Contain("Unreliable").And.Contain("StrictDecoderRejectedBytes");
        PageBaseTypeProjection.Inspect(read).BaseType.Should().BeNull();
    }

    [Fact]
    public async Task Frozen_unknown_content_keeps_a_declaration_candidate_without_promoting_it()
    {
        var row = AspxSourceReaderTests.Row();
        var captured = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Candidate' %>"), row);
        // Supplied acquisition evidence can explicitly leave payload knowledge Unknown.
        var unknown = new AspxSourceReadResult(captured.Discovery, captured.PhysicalIdentity, captured.Version,
            captured.TransportState, captured.TransportReason, captured.CaptureState, captured.OriginalBytes,
            captured.ExpectedByteLength, captured.Decoding, captured.DecodedText, AspxSourceContentState.Unknown,
            "SyntheticFrozenClassificationUnknown");
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 },
            (_, _) => Task.FromResult(unknown), PageBaseTypeProjectionTests.ApplicableConfiguration())(row, default);
        var result = row.PageBaseTypeProjections.Single();
        result.SourceRead.TransportState.Should().Be(AspxSourceTransportState.Complete);
        result.SourceRead.ContentState.Should().Be(AspxSourceContentState.Unknown);
        result.SourceRead.ContentReason.Should().Be("SyntheticFrozenClassificationUnknown");
        result.SourceRead.CapturedByteLength.Should().Be(captured.CapturedByteLength);
        result.SourceRead.ExpectedByteLength.Should().BeNull();
        result.SourceRead.OriginalBytes.Should().Equal(captured.OriginalBytes);
        result.SourceRead.RawDigest.Should().Be(captured.RawDigest);
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.Declared);
        result.DeclaredInherits.Should().Be("Synthetic.Candidate");
        result.IsReliableParse.Should().BeFalse();
        result.IsVerifiedAbsence.Should().BeFalse();
        result.Configuration.KnowledgeState.Should().Be(PageConfigurationKnowledge.EffectiveOverride);
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.BaseType.Should().BeNull();
        result.Reason.Should().Contain("SyntheticFrozenClassificationUnknown");
        row.PageTypeSourceStatus.Should().Be("Unknown");
        row.DeclaredPageType.Should().BeNull();
        row.PublishingLayoutFamily.Should().Be("Unknown");
    }

    [Theory]
    [InlineData("<%@ Page Language='C#' %>", "VerifiedAbsent", null)]
    [InlineData("<%@ Page Inherits='  Synthetic.Partial  ' %>", "Declared", "  Synthetic.Partial  ")]
    [InlineData("<%@ Page Inherits='Synthetic.Prefix", "MalformedDirective", "Synthetic.Prefix")]
    public async Task Partial_captures_retain_declaration_fragments_but_never_project_a_verified_type_or_default(string text, string status, string fragment)
    {
        var row = AspxSourceReaderTests.Row();
        var bytes = Encoding.UTF8.GetBytes(text);
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(AspxSourceReaderTests.Observed), _ => Task.FromResult<Stream>(new AspxSourceReaderTests.InterruptingStream(bytes)), default);
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 },
            (_, _) => Task.FromResult(read), PageBaseTypeProjectionTests.ApplicableConfiguration())(row, default);
        var result = row.PageBaseTypeProjections.Single();
        read.TransportState.Should().Be(AspxSourceTransportState.Partial);
        read.CaptureState.Should().Be(AspxSourceCaptureState.Partial);
        read.ContentState.Should().Be(AspxSourceContentState.Source);
        read.Decoding.State.Should().Be(AspxSourceDecodingState.Reliable);
        read.CapturedByteLength.Should().Be(bytes.Length);
        read.ExpectedByteLength.Should().BeNull();
        read.RawDigestScope.Should().Be("PartialCapturedBytes");
        read.OriginalBytes.Should().Equal(bytes);
        result.Parse.Status.ToString().Should().Be(status);
        result.Parse.Directives.Should().ContainSingle().Which.RawText.Should().Be(text);
        result.DeclaredInherits.Should().Be(fragment);
        result.IsReliableParse.Should().BeFalse();
        result.IsVerifiedAbsence.Should().BeFalse();
        result.BaseType.Should().BeNull();
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.Reason.Should().Contain("StreamReadFailed");
        row.DeclaredPageType.Should().BeNull();
        row.PublishingLayoutFamily.Should().Be("Unknown");
        row.PageTypeSourceStatus.Should().Be("Unknown");
        var family = JsonSerializer.Deserialize<PublishingLayoutTypeEvidence.Observation[]>(row.PageTypeEvidenceJson).Single();
        family.DirectiveEvidence.Should().Be(text);
        family.ParseStatus.Should().Be(status);
        family.ResolvedIdentity.Should().BeNull();
    }

    [Fact]
    public async Task A_truncated_capture_of_a_complete_no_Inherits_directive_cannot_use_either_default()
    {
        var row = AspxSourceReaderTests.Row();
        var bytes = Encoding.UTF8.GetBytes("<%@ Page %>");
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(AspxSourceReaderTests.Observed), _ => Task.FromResult<Stream>(new MemoryStream(bytes)), default, expectedByteLength: 100);
        read.CapturedByteLength.Should().Be(11);
        read.ExpectedByteLength.Should().Be(100);
        read.TransportReason.Should().Be("ExpectedFileLengthMismatch");
        foreach (var configuration in new[] { new PageBaseTypeConfiguration(), PageBaseTypeProjectionTests.ApplicableConfiguration() })
        {
            var result = PageBaseTypeProjection.Inspect(read, configuration);
            result.Parse.Status.Should().Be(PageDirectiveParseStatus.VerifiedAbsent);
            result.IsReliableParse.Should().BeFalse();
            result.IsVerifiedAbsence.Should().BeFalse();
            result.TypeSource.Should().Be(PageTypeSource.Unknown);
            result.BaseType.Should().BeNull();
        }
    }

    [Fact]
    public async Task Reliable_absence_explicit_empty_and_unavailable_have_separate_assertable_states()
    {
        var absence = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page %>")));
        var explicitEmpty = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page Inherits='' %>")));
        var whitespace = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(" \r\n\t ")));
        var unavailable = PageBaseTypeProjection.Inspect(AspxSourceReadResult.Unavailable(AspxSourceReaderTests.Row().DiscoveryObservation,
            AspxSourceTransportState.NotAttempted, "SyntheticNotAttempted"));
        absence.DeclaredInherits.Should().BeNull();
        absence.Parse.Status.Should().Be(PageDirectiveParseStatus.VerifiedAbsent);
        absence.TypeSource.Should().Be(PageTypeSource.FrameworkDefault);
        absence.IsVerifiedAbsence.Should().BeTrue();
        absence.SourceRead.IsReliableSource.Should().BeTrue();
        explicitEmpty.DeclaredInherits.Should().BeEmpty();
        explicitEmpty.SourceRead.IsReliableSource.Should().BeTrue();
        explicitEmpty.Parse.Status.Should().Be(PageDirectiveParseStatus.EmptyInherits);
        explicitEmpty.TypeSource.Should().Be(PageTypeSource.Unknown);
        explicitEmpty.IsVerifiedAbsence.Should().BeFalse();
        whitespace.SourceRead.TransportState.Should().Be(AspxSourceTransportState.Complete);
        whitespace.SourceRead.ContentState.Should().Be(AspxSourceContentState.Empty);
        whitespace.SourceRead.CapturedByteLength.Should().Be(5);
        whitespace.Parse.Status.Should().Be(PageDirectiveParseStatus.EmptySource);
        whitespace.TypeSource.Should().Be(PageTypeSource.Unknown);
        whitespace.IsVerifiedAbsence.Should().BeFalse();
        unavailable.SourceRead.CapturedByteLength.Should().BeNull();
        unavailable.Parse.Status.Should().Be(PageDirectiveParseStatus.SourceUnavailable);
        unavailable.TypeSource.Should().Be(PageTypeSource.Unknown);
        unavailable.IsVerifiedAbsence.Should().BeFalse();
    }

    [Fact]
    public async Task Identity_uncertainty_and_conflicts_suppress_defaults_without_changing_read_outcomes()
    {
        var row = AspxSourceReaderTests.Row();
        foreach (var identity in new[]
        {
            row.DiscoveryObservation.Identity with { FileUniqueId = null },
            row.DiscoveryObservation.Identity with { FileUniqueId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") },
        })
        {
            var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page %>"), row, identity);
            var result = PageBaseTypeProjection.Inspect(read, PageBaseTypeProjectionTests.ApplicableConfiguration());
            read.TransportState.Should().Be(AspxSourceTransportState.Complete);
            read.ContentState.Should().Be(AspxSourceContentState.Source);
            read.IsReliableSource.Should().BeFalse();
            result.Parse.Status.Should().Be(PageDirectiveParseStatus.VerifiedAbsent);
            result.IsReliableParse.Should().BeFalse();
            result.IsVerifiedAbsence.Should().BeFalse();
            result.TypeSource.Should().Be(PageTypeSource.Unknown);
            result.BaseType.Should().BeNull();
            result.Reason.Should().Contain(identity.FileUniqueId == null ? "FileUniqueIdNotReturned" : "DiscoveryFileIdentityChanged");
        }
    }

    [Fact]
    public async Task A_later_source_failure_retains_the_successful_projection_and_every_read_facet()
    {
        var row = AspxSourceReaderTests.Row();
        var success = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Success' %>"), row);
        var scan = new Scan { PublishingLayoutRuleVersion = 1 };
        await AspxSourceAcquisition.ForScan(scan, (_, _) => Task.FromResult(success))(row, default);
        await AspxSourceAcquisition.ForScan(scan, (_, _) => throw new IOException("Synthetic later read failure"))(row, default);
        row.SourceReads.Should().HaveCount(2);
        row.PageBaseTypeProjections.Should().HaveCount(2);
        row.PageBaseTypeProjections[0].TypeSource.Should().Be(PageTypeSource.Declared);
        row.PageBaseTypeProjections[0].BaseType.Should().Be("Synthetic.Success");
        row.PageBaseTypeProjections[0].SourceRead.Should().BeSameAs(success);
        row.PageBaseTypeProjections[1].TypeSource.Should().Be(PageTypeSource.Unknown);
        row.PageBaseTypeProjections[1].SourceRead.TransportState.Should().Be(AspxSourceTransportState.Failed);
        row.PageBaseTypeProjections[1].SourceRead.CapturedByteLength.Should().BeNull();
        row.PageBaseTypeProjections[1].Reason.Should().Contain("Synthetic later read failure");
        row.DiscoveryStatus.Should().Be("Discovered");
        row.AssessmentStatus.Should().BeNull();
    }
}
