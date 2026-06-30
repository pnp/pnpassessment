using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using System.Collections.Generic;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    /// <summary>
    /// Unit tests for the pure functions in <see cref="AuditLogUsageAnalyzer"/>.
    /// The live Management Activity API query (<c>QueryAllSitesAuditUsageAsync</c>) is
    /// integration-only and is not covered here.
    /// </summary>
    public class AuditLogUsageAnalyzerTests
    {
        private const string PageUrl = "https://contoso.sharepoint.com/sites/team/SitePages/Home.aspx";

        private static ClassicPageAuditUsage Record(string pageUrl = PageUrl) =>
            new() { PageUrl = pageUrl };

        private static IReadOnlyDictionary<string, AuditLogUsageAnalyzer.AuditPageStats> Stats(
            string url = PageUrl, int views = 5, int creates = 2, int edits = 3, int users = 4) =>
            new Dictionary<string, AuditLogUsageAnalyzer.AuditPageStats>(StringComparer.OrdinalIgnoreCase)
            {
                [url] = new AuditLogUsageAnalyzer.AuditPageStats(views, creates, edits, users)
            };

        [Fact]
        public void AuditUsage_ApplyAuditUsage_MapsAllCountsOntoRecord()
        {
            var record = Record();

            AuditLogUsageAnalyzer.ApplyAuditUsage(record, Stats());

            record.AuditViewsCount.Should().Be(5);
            record.AuditCreatesCount.Should().Be(2);
            record.AuditEditsCount.Should().Be(3);
            record.AuditUniqueUsers.Should().Be(4);
        }

        [Fact]
        public void AuditUsage_ApplyAuditUsage_NullStats_LeavesCountsAtZero()
        {
            var record = Record();

            AuditLogUsageAnalyzer.ApplyAuditUsage(record, null);

            record.AuditViewsCount.Should().Be(0);
            record.AuditCreatesCount.Should().Be(0);
            record.AuditEditsCount.Should().Be(0);
            record.AuditUniqueUsers.Should().Be(0);
        }

        [Fact]
        public void AuditUsage_ApplyAuditUsage_PageUrlNotInStats_LeavesCountsAtZero()
        {
            var record = Record("https://contoso.sharepoint.com/sites/team/SitePages/Other.aspx");

            AuditLogUsageAnalyzer.ApplyAuditUsage(record, Stats());

            record.AuditViewsCount.Should().Be(0);
            record.AuditCreatesCount.Should().Be(0);
            record.AuditEditsCount.Should().Be(0);
            record.AuditUniqueUsers.Should().Be(0);
        }

        [Fact]
        public void AuditUsage_ApplyAuditUsage_PageUrlMatchIsCaseInsensitive()
        {
            // The stats dictionary uses OrdinalIgnoreCase; URL casing differences must not matter.
            var record = Record(PageUrl.ToUpperInvariant());

            AuditLogUsageAnalyzer.ApplyAuditUsage(record, Stats(url: PageUrl.ToLowerInvariant()));

            record.AuditViewsCount.Should().Be(5);
            record.AuditUniqueUsers.Should().Be(4);
        }

        [Fact]
        public void AuditUsage_ApplyAuditUsage_ZeroCounts_StillApplied()
        {
            // A page that was found in the audit window but had 0 events on all dimensions
            // must explicitly write 0s (not be skipped by an accidental null-check).
            var record = Record();

            AuditLogUsageAnalyzer.ApplyAuditUsage(record, Stats(views: 0, creates: 0, edits: 0, users: 0));

            record.AuditViewsCount.Should().Be(0);
            record.AuditCreatesCount.Should().Be(0);
            record.AuditEditsCount.Should().Be(0);
            record.AuditUniqueUsers.Should().Be(0);
        }

        // ── SplitWindow ──────────────────────────────────────────────────────────

        [Fact]
        public void SplitWindow_ExactMultiple_ProducesEvenChunks()
        {
            var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var end   = new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc); // 6 days / 2 = 3 chunks

            var chunks = AuditLogUsageAnalyzer.SplitWindow(start, end, chunkDays: 2);

            chunks.Should().HaveCount(3);
            chunks[0].Should().Be((start, start.AddDays(2)));
            chunks[1].Should().Be((start.AddDays(2), start.AddDays(4)));
            chunks[2].Should().Be((start.AddDays(4), end));
        }

        [Fact]
        public void SplitWindow_NonExactMultiple_LastChunkShorter()
        {
            var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var end   = new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc); // 5 days / 2 = 2 full + 1 partial

            var chunks = AuditLogUsageAnalyzer.SplitWindow(start, end, chunkDays: 2);

            chunks.Should().HaveCount(3);
            chunks[2].End.Should().Be(end);                          // last chunk ends exactly at window end
            (chunks[2].End - chunks[2].Start).TotalDays.Should().Be(1); // last chunk is 1 day
        }

        [Fact]
        public void SplitWindow_WindowSmallerThanChunk_ProducesSingleChunk()
        {
            var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var end   = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc); // 1 day < ChunkDays=2

            var chunks = AuditLogUsageAnalyzer.SplitWindow(start, end, chunkDays: 2);

            chunks.Should().HaveCount(1);
            chunks[0].Start.Should().Be(start);
            chunks[0].End.Should().Be(end);
        }

        // ── MergeChunks ──────────────────────────────────────────────────────────

        [Fact]
        public void MergeChunks_SeparatePages_CombinesAllEntries()
        {
            var chunk1 = new Dictionary<string, AuditLogUsageAnalyzer.AuditPageStats>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://contoso.sharepoint.com/sites/s/SitePages/A.aspx"] = new(3, 1, 0, 2),
            };
            var chunk2 = new Dictionary<string, AuditLogUsageAnalyzer.AuditPageStats>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://contoso.sharepoint.com/sites/s/SitePages/B.aspx"] = new(5, 0, 2, 3),
            };

            var merged = AuditLogUsageAnalyzer.MergeChunks(new[] { chunk1, chunk2 });

            merged.Should().HaveCount(2);
            merged["https://contoso.sharepoint.com/sites/s/SitePages/A.aspx"].ViewsCount.Should().Be(3);
            merged["https://contoso.sharepoint.com/sites/s/SitePages/B.aspx"].ViewsCount.Should().Be(5);
        }

        [Fact]
        public void MergeChunks_SamePage_SumsCountsAcrossChunks()
        {
            const string url = "https://contoso.sharepoint.com/sites/s/SitePages/Home.aspx";
            var chunk1 = new Dictionary<string, AuditLogUsageAnalyzer.AuditPageStats>(StringComparer.OrdinalIgnoreCase)
                { [url] = new(3, 1, 0, 2) };
            var chunk2 = new Dictionary<string, AuditLogUsageAnalyzer.AuditPageStats>(StringComparer.OrdinalIgnoreCase)
                { [url] = new(2, 0, 1, 1) };

            var merged = AuditLogUsageAnalyzer.MergeChunks(new[] { chunk1, chunk2 });

            merged.Should().HaveCount(1);
            merged[url].ViewsCount.Should().Be(5);   // 3 + 2
            merged[url].CreatesCount.Should().Be(1); // 1 + 0
            merged[url].EditsCount.Should().Be(1);   // 0 + 1
            merged[url].UniqueUsers.Should().Be(3);  // 2 + 1 (intentional over-count across chunks)
        }

        [Fact]
        public void MergeChunks_EmptyInput_ReturnsEmptyDict()
        {
            var merged = AuditLogUsageAnalyzer.MergeChunks(
                Enumerable.Empty<IReadOnlyDictionary<string, AuditLogUsageAnalyzer.AuditPageStats>>());

            merged.Should().BeEmpty();
        }
    }
}
