using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

[Trait("Category", "PageInherits")]
public sealed class PageSourcePersistenceTests
{
    private const string Declaration = "  Synthetic.Custom, \"quoted\"\nType  ";
    private const string Source = "<%@ Page Language='C#' Inherits='" + Declaration + "' %>";

    [Theory]
    [InlineData("utf-8", false, 0)]
    [InlineData("utf-8", true, 3)]
    [InlineData("utf-16le", true, 2)]
    [InlineData("utf-16be", true, 2)]
    public async Task Native_write_close_reopen_preserves_exact_bytes_identity_version_projection_and_scope(string encoding, bool bom, int bomLength)
    {
        using var fixture = new PageSourcePersistenceFixture();
        var config = ApplicableConfiguration();
        var scan = await fixture.SeedAsync(config);
        var row = fixture.Page();
        var bytes = Encode(Source, encoding, bom);
        var physical = row.DiscoveryObservation.Identity with
        {
            Url = PageSourcePersistenceFixture.Web + "/Forms/renamed.aspx", Name = "renamed.aspx",
        };
        var read = await PageSourcePersistenceFixture.Capture(row, bytes, physical);
        await fixture.AcquireAsync(scan, row, read);
        var reopened = await fixture.ReopenAsync(row.RecordKey);
        reopened.Url.Should().Be("/sites/source/Forms/original.aspx");
        reopened.FileName.Should().Be("original.aspx");
        reopened.SiteCollectionId.Should().Be(PageSourcePersistenceFixture.SiteId);
        reopened.WebId.Should().Be(PageSourcePersistenceFixture.WebId);
        reopened.FileUniqueId.Should().Be(PageSourcePersistenceFixture.FileId);
        reopened.ListId.Should().Be(PageSourcePersistenceFixture.ListId);
        reopened.ListItemId.Should().Be(7);
        reopened.OriginalDiscoveryUrl.Should().Be("/sites/source/Forms/original.aspx");
        reopened.OriginalDiscoveryFileName.Should().Be("original.aspx");
        reopened.OriginalDiscoveryObservedAtUtc.Should().Be("2026-02-03T04:05:06.1234567Z");
        reopened.SourceUrl.Should().Be("/sites/source/Forms/renamed.aspx");
        reopened.SourceFileName.Should().Be("renamed.aspx");
        reopened.SourceObservedAtUtc.Should().Be("2026-02-03T04:05:07.7654321Z");
        reopened.SourceETag.Should().Be("\"synthetic, version\"");
        reopened.SourceVersionState.Should().Be("ObservedVersion");
        reopened.SourceMajorVersion.Should().Be(3);
        reopened.SourceMinorVersion.Should().Be(7);
        reopened.DeclaredInherits.Should().Be(Declaration);
        reopened.NormalizedInherits.Should().Be("Synthetic.Custom, \"quoted\"\nType");
        reopened.BaseType.Should().Be("Synthetic.Custom, \"quoted\"\nType");
        reopened.TypeSource.Should().Be("Declared");
        reopened.BaseTypeReason.Should().Be("ExplicitPageInherits;NotRuntimeTypeProof");
        reopened.PageParseState.Should().Be("Declared");
        reopened.PageParseReliable.Should().BeTrue();
        reopened.VerifiedInheritsAbsence.Should().BeFalse();
        reopened.ConfigurationKnowledgeState.Should().Be("EffectiveOverride");
        reopened.ConfigurationApplicability.Should().Be("Applicable", "configuration scope uses physical identity/path");
        reopened.EffectivePagesPageBaseType.Should().Be("  Synthetic.Configured  ");
        reopened.PageBaseTypeProvenance.Should().Be("Synthetic, \"configuration\"\nprovenance");
        reopened.SourceReadState.Should().Be("Complete");
        reopened.SourceReadReason.Should().Be("StreamEndedNormally");
        reopened.SourceCaptureState.Should().Be("Complete");
        reopened.SourceDecodingState.Should().Be("Reliable");
        reopened.SourceEncoding.Should().Be(encoding);
        reopened.SourceContentState.Should().Be("Source");
        reopened.SourceContentReason.Should().Be("PhysicalDownloadWithServerSourcePreamble");
        reopened.SourceCapturedByteLength.Should().Be(bytes.LongLength);
        reopened.SourceExpectedByteLength.Should().Be(bytes.LongLength);
        reopened.SourceRawDigestAlgorithm.Should().Be("SHA256");
        reopened.SourceRawDigestScope.Should().Be("CompleteCapturedResponse");
        var expectedDigest = Convert.ToHexString(SHA256.HashData(bytes));
        reopened.SourceRawDigest.Should().Be(expectedDigest);
        var observation = reopened.ReadSourceEvidence().Reads.Should().ContainSingle().Which;
        observation.DecodingReason.Should().Be(bom ? "BomSelectedStrictDecoder" : "StrictUtf8AssumptionWithoutBom");
        observation.BomByteCount.Should().Be(bomLength);
        observation.Discovery.DiscoveryRecord.Metadata["original, \"metadata\""].Should().Be("raw\nvalue");
        observation.Discovery.DiscoveryRecord.FileUniqueId.Should().Be("33333333-3333-3333-3333-333333333333");
        observation.PhysicalIdentity.State.Should().Be("Resolved");
        observation.PhysicalIdentity.ListState.Should().Be("Observed");
        observation.PhysicalIdentity.ItemState.Should().Be("Observed");
        observation.Page.ConfigurationEvidence.Should().ContainSingle().Which.FileScope.ServerRelativePath
            .Should().Be("/sites/source/Forms/renamed.aspx");
        observation.Page.Directives.Should().ContainSingle().Which.RawText.Should().Be(Source);
        reopened.SourceArtifactReference.Should().Be(
            $"assessment.db#cp1/{fixture.ScanId:D}/{Uri.EscapeDataString(row.RecordKey)}/{observation.ObservationId}");
        var retrieved = await fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, row.RecordKey, observation.ObservationId);
        retrieved.Should().Equal(bytes);
        Convert.ToHexString(SHA256.HashData(retrieved)).Should().Be(expectedDigest);
        retrieved[0] ^= 0xff;
        (await fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, row.RecordKey, observation.ObservationId)).Should().Equal(bytes);
        observation.LegacySourceHash.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source))));
        observation.LegacySourceHashKind.Should().Be("DecodedTextUtf8Sha256");
        reopened.SourceReads.Should().BeEmpty("reopened persistence is a durable document, not re-executed transient acquisition");
        reopened.PageBaseTypeProjections.Should().BeEmpty();
    }

    [Fact]
    public async Task Equal_text_in_different_encodings_keeps_distinct_raw_digests_and_a_recognizable_legacy_text_hash()
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        foreach (var bytes in new[] { Encode(Source, "utf-8", false), Encode(Source, "utf-16be", true) })
        {
            var row = fixture.Page();
            await fixture.AcquireAsync(scan, row, await PageSourcePersistenceFixture.Capture(row, bytes));
        }
        var retained = await fixture.ReopenAsync(fixture.Page().RecordKey);
        var reads = retained.ReadSourceEvidence().Reads;
        reads.Should().HaveCount(2);
        reads.Select(value => value.RawDigest).Distinct().Should().HaveCount(2);
        reads.Select(value => value.LegacySourceHash).Distinct().Should().ContainSingle();
        foreach (var observation in reads)
            Convert.ToHexString(SHA256.HashData(await fixture.Writer().ReadSourceArtifactAsync(
                fixture.ScanId, retained.RecordKey, observation.ObservationId))).Should().Be(observation.RawDigest);
    }

    [Theory]
    [InlineData("Empty", "Complete", "Complete", "Reliable", "Empty", "EmptySource", 0)]
    [InlineData("DecodeFailure", "Complete", "Complete", "Unreliable", "Unknown", "SourceUnavailable", 1)]
    [InlineData("Partial", "Partial", "Partial", "Reliable", "Source", "Declared", 42)]
    [InlineData("DeniedBody", "Denied", "Complete", "Reliable", "Source", "Declared", 42)]
    public async Task Bytes_and_lexical_fragments_survive_independent_read_decode_and_parse_failures(string kind,
        string transport, string capture, string decode, string content, string parse, int length)
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        var row = fixture.Page(item: null);
        var bytes = kind == "Empty" ? Array.Empty<byte>() : kind == "DecodeFailure" ? new byte[] { 0xc3 } :
            Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Candidate' %>");
        // Hand-listed expected length, not the captured result.
        bytes.Length.Should().Be(length);
        var read = await PageSourcePersistenceFixture.Capture(row, bytes,
            expected: kind == "Partial" ? 100 : null, httpStatus: kind == "DeniedBody" ? 403 : null);
        await fixture.AcquireAsync(scan, row, read);
        var reopened = await fixture.ReopenAsync(row.RecordKey);
        reopened.SourceReadState.Should().Be(transport);
        reopened.SourceCaptureState.Should().Be(capture);
        reopened.SourceDecodingState.Should().Be(decode);
        reopened.SourceContentState.Should().Be(content);
        reopened.PageParseState.Should().Be(parse);
        reopened.PageParseReliable.Should().BeFalse();
        reopened.BaseType.Should().BeNull();
        reopened.TypeSource.Should().Be("Unknown");
        reopened.VerifiedInheritsAbsence.Should().BeFalse();
        reopened.PublishingLayoutFamily.Should().Be("Unknown");
        reopened.AssessmentStatus.Should().BeNull("source failure is not a whole-page skip");
        reopened.ListId.Should().BeNull();
        reopened.ListItemId.Should().BeNull();
        var observation = reopened.ReadSourceEvidence().Reads.Single();
        observation.PhysicalIdentity.ListState.Should().Be("Unavailable");
        observation.PhysicalIdentity.ItemState.Should().Be("Unavailable");
        observation.Page.DeclaredInherits.Should().Be(kind is "Partial" or "DeniedBody" ? "Synthetic.Candidate" : null);
        observation.HttpStatusCode.Should().Be(kind == "DeniedBody" ? 403 : null);
        observation.RawDigestScope.Should().Be(kind == "Partial" ? "PartialCapturedBytes" : "CompleteCapturedResponse");
        (await fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, row.RecordKey, observation.ObservationId)).Should().Equal(bytes);
        observation.RawDigest.Should().Be(Convert.ToHexString(SHA256.HashData(bytes)));
        observation.ReadReason.Should().Be(kind == "Partial" ? "ExpectedFileLengthMismatch" :
            kind == "DeniedBody" ? "TransportHTTP403;StreamEndedNormally" : "StreamEndedNormally");
    }

    [Theory]
    [InlineData("NotAttempted")]
    [InlineData("NotReturned")]
    [InlineData("Denied")]
    [InlineData("Failed")]
    public async Task Unavailable_reads_are_not_fabricated_empty_bytes_versions_or_successes(string state)
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        var row = fixture.Page(item: null);
        var read = AspxSourceReadResult.Unavailable(row.DiscoveryObservation, Enum.Parse<AspxSourceTransportState>(state),
            "Synthetic, \"reason\"\n" + state, version: new(PageSourcePersistenceFixture.ReadTime));
        await fixture.AcquireAsync(scan, row, read);
        var reopened = await fixture.ReopenAsync(row.RecordKey);
        reopened.SourceReadState.Should().Be(state);
        reopened.SourceCapturedByteLength.Should().BeNull();
        reopened.SourceExpectedByteLength.Should().BeNull();
        reopened.SourceRawDigest.Should().BeNull();
        reopened.SourceRawDigestAlgorithm.Should().BeNull();
        reopened.SourceArtifactReference.Should().BeNull();
        reopened.SourceVersionState.Should().Be("ObservationTimeOnly");
        reopened.SourceVersionReason.Should().Be("VersionMetadataNotReturned;SourceObservationUtcRetained");
        reopened.SourceObservedAtUtc.Should().Be("2026-02-03T04:05:07.7654321Z");
        reopened.SourceReadReason.Should().Be("Synthetic, \"reason\"\n" + state);
        reopened.PageParseState.Should().Be("SourceUnavailable");
        reopened.TypeSource.Should().Be("Unknown");
        reopened.BaseType.Should().BeNull();
        reopened.SourceCaptureState.Should().Be("NotCaptured");
        reopened.SourceDecodingState.Should().Be("NotAttempted");
        reopened.SourceContentState.Should().Be("NotInspected");
        reopened.ReadSourceEvidence().Reads.Single().OriginalBytes.Should().BeNull();
        (await fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, row.RecordKey, reopened.SourceObservationId)).Should().BeNull();
    }

    [Fact]
    public async Task Metadata_replay_rename_version_change_and_other_file_at_same_url_preserve_every_original_observation()
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        var first = fixture.Page();
        var bytes = Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.First' %>");
        await fixture.AcquireAsync(scan, first, await PageSourcePersistenceFixture.Capture(first, bytes));
        var firstStored = await fixture.ReopenAsync(first.RecordKey);
        var originalEvidence = firstStored.SourceEvidenceJson;
        // Same instance and already persisted read must be idempotent, not a new acquisition.
        await fixture.Writer().WriteAsync(new[] { first, firstStored, firstStored });
        (await fixture.ReopenAsync(first.RecordKey)).SourceEvidenceJson.Should().Be(originalEvidence);
        var metadata = new ClassicPageDiscovery
        {
            ScanId = fixture.ScanId, RecordKey = first.RecordKey, RowType = "Page", DiscoveryStatus = "Discovered",
            ContentTypeId = AspxAssetPurpose.LayoutContentType, PageType = "WebPartPage", AssessmentStatus = "Complete",
        };
        await fixture.Writer().UpdateExistingAsync(new[] { metadata });
        var afterMetadata = await fixture.ReopenAsync(first.RecordKey);
        afterMetadata.SourceEvidenceJson.Should().Be(originalEvidence);
        afterMetadata.DeclaredInherits.Should().Be("Synthetic.First");
        afterMetadata.AssessmentStatus.Should().Be("Complete");
        var renamed = fixture.Page("renamed.aspx");
        renamed.ObservedAtUtc = PageSourcePersistenceFixture.DiscoveryTime.AddMinutes(1).UtcDateTime;
        renamed.DiscoveryObservation = AspxFileObservation.FromDiscovery(renamed, renamed.DiscoveryObservation.DiscoveryRecord);
        await fixture.AcquireAsync(scan, renamed, await PageSourcePersistenceFixture.Capture(renamed,
            Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Second' %>"), version: new(
                PageSourcePersistenceFixture.ReadTime.AddMinutes(1), "\"v2\"", 4, 0)));
        var failed = fixture.Page("renamed.aspx");
        await fixture.AcquireAsync(scan, failed, AspxSourceReadResult.Unavailable(failed.DiscoveryObservation,
            AspxSourceTransportState.Denied, "Synthetic later denial"));
        var stored = await fixture.ReopenAsync(first.RecordKey);
        stored.Url.Should().Be("/sites/source/Forms/renamed.aspx");
        stored.FileName.Should().Be("renamed.aspx");
        stored.OriginalDiscoveryUrl.Should().Be("/sites/source/Forms/original.aspx");
        stored.OriginalDiscoveryFileName.Should().Be("original.aspx");
        var history = stored.ReadSourceEvidence();
        history.DiscoveryObservations.Should().HaveCount(3);
        history.Reads.Should().HaveCount(3);
        history.Reads.Select(read => read.ObservationId).Distinct().Should().HaveCount(3);
        history.Reads[0].Page.DeclaredInherits.Should().Be("Synthetic.First");
        history.Reads[0].Discovery.Identity.Name.Should().Be("original.aspx");
        history.Reads[1].Page.DeclaredInherits.Should().Be("Synthetic.Second");
        history.Reads[1].ETag.Should().Be("\"v2\"");
        history.Reads[1].MajorVersion.Should().Be(4);
        history.Reads[1].MinorVersion.Should().Be(0);
        history.Reads[1].Discovery.Identity.Name.Should().Be("renamed.aspx");
        history.Reads[2].ReadState.Should().Be("Denied");
        history.Reads[2].OriginalBytes.Should().BeNull();
        stored.SourceReadState.Should().Be("Denied", "the latest observation cannot masquerade as an earlier successful read");
        stored.TypeSource.Should().Be("Unknown");
        stored.BaseType.Should().BeNull();
        stored.DiscoveryStatus.Should().Be("Discovered");
        stored.AssessmentStatus.Should().Be("Complete", "a source-facet failure cannot erase successful page assessment");
        (await fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, first.RecordKey, history.Reads[0].ObservationId)).Should().Equal(bytes);
        var other = fixture.Page("renamed.aspx", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        await fixture.AcquireAsync(scan, other, await PageSourcePersistenceFixture.Capture(other, Encoding.UTF8.GetBytes("<%@ Page %>")));
        other.RecordKey.Should().NotBe(first.RecordKey, "URL is not physical identity");
        (await fixture.Writer().ReadPagesAsync(fixture.ScanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web))
            .Should().HaveCount(2);
        (await fixture.ReopenAsync(first.RecordKey)).ReadSourceEvidence().Reads.Should().HaveCount(3);
        (await fixture.ReopenAsync(other.RecordKey)).ReadSourceEvidence().Reads.Should().ContainSingle().Which.PhysicalIdentity.FileUniqueId
            .Should().Be(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    }

    [Fact]
    public async Task Corrupt_or_unsupported_evidence_fails_closed_instead_of_silently_losing_bytes_or_inventing_an_artifact()
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        var row = fixture.Page();
        await fixture.AcquireAsync(scan, row, await PageSourcePersistenceFixture.Capture(row, Encoding.UTF8.GetBytes("<%@ Page %>")));
        var retained = await fixture.ReopenAsync(row.RecordKey);
        var original = retained.SourceEvidenceJson;
        var digest = retained.SourceRawDigest;
        retained.SourceEvidenceJson = original.Replace(digest, new string('0', 64), StringComparison.Ordinal);
        using (var db = fixture.CreateContext()) { db.ClassicPageDiscoveries.Update(retained); await db.SaveChangesAsync(); }
        var retrieve = () => fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, row.RecordKey, retained.SourceObservationId);
        await retrieve.Should().ThrowAsync<InvalidDataException>();
        retained.SourceEvidenceJson = """{"Version":99,"DiscoveryObservations":[],"Reads":[]}""";
        using (var db = fixture.CreateContext()) { db.ClassicPageDiscoveries.Update(retained); await db.SaveChangesAsync(); }
        var update = () => fixture.Writer().UpdateExistingAsync(new[] { fixture.Page() });
        await update.Should().ThrowAsync<InvalidDataException>();
        (await fixture.ReopenAsync(row.RecordKey)).SourceEvidenceJson.Should().Be(retained.SourceEvidenceJson);
    }

    [Fact]
    public async Task Unresolved_physical_identity_and_unknown_expected_length_keep_states_reasons_and_the_original_raw_record()
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        var raw = new RawDiscoveryRecord("Synthetic unresolved record", "not-a-guid", "files", "unresolved.aspx",
            "/sites/source/unresolved.aspx", true, "synthetic", SiteCollectionId: null, WebId: PageSourcePersistenceFixture.WebId);
        var row = AssessmentWebDiscovery.Page(fixture.ScanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web,
            new("files", "web", DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles, "/sites/source", "synthetic"), raw, 1);
        var bytes = Encoding.UTF8.GetBytes("<%@ Page %>");
        var read = await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
            new(PageSourcePersistenceFixture.ReadTime), _ => Task.FromResult<Stream>(new MemoryStream(bytes)), default);
        await fixture.AcquireAsync(scan, row, read);
        var reopened = await fixture.ReopenAsync(row.RecordKey);
        reopened.RecordKey.Should().StartWith("page:unresolved:");
        reopened.FileUniqueId.Should().BeNull();
        reopened.SiteCollectionId.Should().BeNull();
        reopened.SourceFileUniqueId.Should().BeNull();
        reopened.SourceSiteCollectionId.Should().BeNull();
        reopened.SourceWebId.Should().Be(PageSourcePersistenceFixture.WebId);
        reopened.SourceIdentityState.Should().Be("Unresolved");
        reopened.SourceIdentityReason.Should().Be("SiteCollectionIdNotReturned;FileUniqueIdNotReturned");
        reopened.SourceReadState.Should().Be("Complete", "transport remains independent of unresolved identity");
        reopened.SourceCapturedByteLength.Should().Be(11);
        reopened.SourceExpectedByteLength.Should().BeNull();
        reopened.PageParseState.Should().Be("VerifiedAbsent", "lexical absence is independent of physical-source authority");
        reopened.PageParseReliable.Should().BeFalse();
        reopened.VerifiedInheritsAbsence.Should().BeFalse();
        reopened.BaseType.Should().BeNull();
        reopened.TypeSource.Should().Be("Unknown");
        var retained = reopened.ReadSourceEvidence().Reads.Single();
        retained.Discovery.DiscoveryRecord.FileUniqueId.Should().Be("not-a-guid");
        retained.Discovery.DiscoveryRecord.SourceObjectId.Should().Be("Synthetic unresolved record");
        retained.VersionState.Should().Be("ObservationTimeOnly");
        retained.ExpectedLengthState.Should().Be("NotReturned");
        retained.ExpectedLengthReason.Should().Be("ExpectedFileByteLengthNotReturned");
        retained.Page.Reason.Should().Contain("SiteCollectionIdNotReturned").And.Contain("FileUniqueIdNotReturned");
        (await fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, row.RecordKey, retained.ObservationId)).Should().Equal(bytes);
    }

    internal static PageBaseTypeConfiguration ApplicableConfiguration() => new(new[]
    {
        new PageBaseTypeConfigurationEvidence("EffectiveOverride", "  Synthetic.Configured  ",
            "Synthetic, \"configuration\"\nprovenance", new("11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333",
                "/sites/source/Forms/renamed.aspx"), "Synthetic, \"configured\"\nreason"),
    });

    private static byte[] Encode(string text, string name, bool bom)
    {
        Encoding encoding = name switch
        {
            "utf-16le" => new UnicodeEncoding(false, bom, true),
            "utf-16be" => new UnicodeEncoding(true, bom, true),
            _ => new UTF8Encoding(bom, true),
        };
        return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
    }
}
