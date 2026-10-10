using FluentAssertions;
using PnP.Scanning.Core.Pipeline.Analysis;
using PnP.Scanning.Core.Pipeline.Collection;
using ClassicPageAuditUsage = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageAuditUsageRow;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline.Analysis
{
    /// <summary>Tests audit window partitioning and offline usage aggregation.</summary>
    public class ClassicPageAuditAnalysisTests
    {
        private const string PageUrl = "https://contoso.sharepoint.com/sites/team/SitePages/Home.aspx";

        [Fact]
        public void LegacyRecords_MultipleRecordPages_PreserveCountsAndDistinctUsers()
        {
            using var first = JsonDocument.Parse("""
                [
                  {"operation":"ClassicPageViewed","objectId":"https://contoso.sharepoint.com/sites/team/SitePages/Home.aspx","userId":"alice"},
                  {"operation":"ClassicPageCreated","objectId":"https://contoso.sharepoint.com/sites/team/SitePages/Home.aspx","userId":"bob"},
                  {"operation":"ClassicPageViewed","objectId":"https://contoso.sharepoint.com/sites/team/doc.docx","userId":"carol"}
                ]
                """);
            using var second = JsonDocument.Parse("""
                [
                  {"operation":"classicpageviewed","objectId":"https://contoso.sharepoint.com/sites/team/SitePages/Home.aspx","userId":"ALICE"},
                  {"operation":"ClassicPageEdited","objectId":"https://contoso.sharepoint.com/sites/team/SitePages/Home.aspx","userId":"BOB"},
                  {"operation":"ClassicPageViewed","userId":"carol"}
                ]
                """);
            var records = new Dictionary<string, ClassicPageAuditAnalysis.ChunkPageData>(StringComparer.OrdinalIgnoreCase);
            ClassicPageAuditAnalysis.AccumulateLegacyRecords(records, first.RootElement);
            ClassicPageAuditAnalysis.AccumulateLegacyRecords(records, second.RootElement);
            var merged = ClassicPageAuditAnalysis.MergeChunks(new[] { records });

            merged.Should().ContainSingle();
            merged[PageUrl].Should().Be(new ClassicPageAuditAnalysis.AuditPageStats(2, 1, 1, 2));
        }

        private static ClassicPageAuditUsage Record(string pageUrl = PageUrl) =>
            new() { PageUrl = pageUrl };

        private static IReadOnlyDictionary<string, ClassicPageAuditAnalysis.AuditPageStats> Stats(
            string url = PageUrl, int views = 5, int creates = 2, int edits = 3, int users = 4) =>
            new Dictionary<string, ClassicPageAuditAnalysis.AuditPageStats>(StringComparer.OrdinalIgnoreCase)
            {
                [url] = new ClassicPageAuditAnalysis.AuditPageStats(views, creates, edits, users)
            };

        [Fact]
        public void AuditUsage_ApplyAuditUsage_MapsAllCountsOntoRecord()
        {
            var record = Record();

            ClassicPageAuditAnalysis.ApplyAuditUsage(record, Stats());

            record.AuditViewsCount.Should().Be(5);
            record.AuditCreatesCount.Should().Be(2);
            record.AuditEditsCount.Should().Be(3);
            record.AuditUniqueUsers.Should().Be(4);
        }

        [Fact]
        public void AuditUsage_ApplyAuditUsage_NullStats_LeavesCountsAtZero()
        {
            var record = Record();

            ClassicPageAuditAnalysis.ApplyAuditUsage(record, null);

            record.AuditViewsCount.Should().Be(0);
            record.AuditCreatesCount.Should().Be(0);
            record.AuditEditsCount.Should().Be(0);
            record.AuditUniqueUsers.Should().Be(0);
        }

        [Fact]
        public void AuditUsage_ApplyAuditUsage_PageUrlNotInStats_LeavesCountsAtZero()
        {
            var record = Record("https://contoso.sharepoint.com/sites/team/SitePages/Other.aspx");

            ClassicPageAuditAnalysis.ApplyAuditUsage(record, Stats());

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

            ClassicPageAuditAnalysis.ApplyAuditUsage(record, Stats(url: PageUrl.ToLowerInvariant()));

            record.AuditViewsCount.Should().Be(5);
            record.AuditUniqueUsers.Should().Be(4);
        }

        [Fact]
        public void AuditUsage_ApplyAuditUsage_ZeroCounts_StillApplied()
        {
            // A page that was found in the audit window but had 0 events on all dimensions
            // must explicitly write 0s (not be skipped by an accidental null-check).
            var record = Record();

            ClassicPageAuditAnalysis.ApplyAuditUsage(record, Stats(views: 0, creates: 0, edits: 0, users: 0));

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

            var chunks = ClassicPageAuditClient.SplitWindow(start, end, chunkDays: 2);

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

            var chunks = ClassicPageAuditClient.SplitWindow(start, end, chunkDays: 2);

            chunks.Should().HaveCount(3);
            chunks[2].End.Should().Be(end);                          // last chunk ends exactly at window end
            (chunks[2].End - chunks[2].Start).TotalDays.Should().Be(1); // last chunk is 1 day
        }

        [Fact]
        public void SplitWindow_WindowSmallerThanChunk_ProducesSingleChunk()
        {
            var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var end   = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc); // 1 day < ChunkDays=2

            var chunks = ClassicPageAuditClient.SplitWindow(start, end, chunkDays: 2);

            chunks.Should().HaveCount(1);
            chunks[0].Start.Should().Be(start);
            chunks[0].End.Should().Be(end);
        }

        // ── MergeChunks ──────────────────────────────────────────────────────────

        [Fact]
        public void MergeChunks_SeparatePages_CombinesAllEntries()
        {
            var chunk1 = new Dictionary<string, ClassicPageAuditAnalysis.ChunkPageData>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://contoso.sharepoint.com/sites/s/SitePages/A.aspx"] = new(3, 1, 0, new HashSet<int> { 1, 2 }),
            };
            var chunk2 = new Dictionary<string, ClassicPageAuditAnalysis.ChunkPageData>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://contoso.sharepoint.com/sites/s/SitePages/B.aspx"] = new(5, 0, 2, new HashSet<int> { 1, 2, 3 }),
            };

            var merged = ClassicPageAuditAnalysis.MergeChunks(new[] { chunk1, chunk2 });

            merged.Should().HaveCount(2);
            merged["https://contoso.sharepoint.com/sites/s/SitePages/A.aspx"].ViewsCount.Should().Be(3);
            merged["https://contoso.sharepoint.com/sites/s/SitePages/B.aspx"].ViewsCount.Should().Be(5);
        }

        [Fact]
        public void MergeChunks_SamePage_SumsCountsAcrossChunks()
        {
            const string url = "https://contoso.sharepoint.com/sites/s/SitePages/Home.aspx";
            // Users 1 and 2 appear in chunk1; user 1 also appears in chunk2 (cross-chunk dedup case)
            var chunk1 = new Dictionary<string, ClassicPageAuditAnalysis.ChunkPageData>(StringComparer.OrdinalIgnoreCase)
                { [url] = new(3, 1, 0, new HashSet<int> { 1, 2 }) };
            var chunk2 = new Dictionary<string, ClassicPageAuditAnalysis.ChunkPageData>(StringComparer.OrdinalIgnoreCase)
                { [url] = new(2, 0, 1, new HashSet<int> { 1 }) };

            var merged = ClassicPageAuditAnalysis.MergeChunks(new[] { chunk1, chunk2 });

            merged.Should().HaveCount(1);
            merged[url].ViewsCount.Should().Be(5);   // 3 + 2
            merged[url].CreatesCount.Should().Be(1); // 1 + 0
            merged[url].EditsCount.Should().Be(1);   // 0 + 1
            merged[url].UniqueUsers.Should().Be(2);  // union({1,2}, {1}) = {1,2} — cross-chunk dedup
        }

        [Fact]
        public void MergeChunks_EmptyInput_ReturnsEmptyDict()
        {
            var merged = ClassicPageAuditAnalysis.MergeChunks(
                Enumerable.Empty<IReadOnlyDictionary<string, ClassicPageAuditAnalysis.ChunkPageData>>());

            merged.Should().BeEmpty();
        }

        [Fact]
        public void MergeChunks_UnionExceedingCap_IsBoundedToMaxTrackedUsersPerPage()
        {
            // A hot page appearing in multiple chunks, each contributing distinct users, must not let
            // the merged distinct-user set grow past the per-page cap (MaxTrackedUsersPerPage = 10,000).
            const string url = "https://contoso.sharepoint.com/sites/s/SitePages/Hot.aspx";
            const int cap = 10_000;

            // chunk1: users 0..7999, chunk2: users 8000..15999 — 16,000 distinct hashes total across chunks.
            var users1 = new HashSet<int>(Enumerable.Range(0, 8_000));
            var users2 = new HashSet<int>(Enumerable.Range(8_000, 8_000));
            var chunk1 = new Dictionary<string, ClassicPageAuditAnalysis.ChunkPageData>(StringComparer.OrdinalIgnoreCase)
                { [url] = new(8_000, 0, 0, users1) };
            var chunk2 = new Dictionary<string, ClassicPageAuditAnalysis.ChunkPageData>(StringComparer.OrdinalIgnoreCase)
                { [url] = new(8_000, 0, 0, users2) };

            var merged = ClassicPageAuditAnalysis.MergeChunks(new[] { chunk1, chunk2 });

            // Counts still sum; the distinct-user set is capped rather than reaching 16,000.
            merged[url].ViewsCount.Should().Be(16_000);
            merged[url].UniqueUsers.Should().Be(cap);
        }
    }
}
