using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Globalization;
using System.Text;
using Scan = PnP.Scanning.Core.Storage.Scan;

namespace PnP.Scanning.Core.Tests.Fixtures;

/// <summary>Wholly synthetic, on-disk native storage with real close/reopen boundaries.</summary>
internal sealed class PageSourcePersistenceFixture : IDisposable
{
    internal const string Site = "https://example.com/sites/source";
    internal const string Web = "/sites/source";
    internal static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid WebId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid FileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid ListId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly DateTimeOffset DiscoveryTime = DateTimeOffset.Parse("2026-02-03T04:05:06.1234567Z", CultureInfo.InvariantCulture);
    internal static readonly DateTimeOffset ReadTime = DateTimeOffset.Parse("2026-02-03T06:35:07.7654321+02:30", CultureInfo.InvariantCulture);
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "synthetic-page-source-" + Guid.NewGuid().ToString("N"));
    internal string DatabasePath => Path.Combine(DirectoryPath, "assessment.db");
    internal Guid ScanId { get; } = Guid.NewGuid();

    internal PageSourcePersistenceFixture(bool migrate = true)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (migrate) { using var db = CreateContext(); db.Database.Migrate(); }
    }

    internal ScanContext CreateContext() => new(new DbContextOptionsBuilder<ScanContext>()
        .UseSqlite($"Data Source={DatabasePath};Pooling=False").Options);
    internal AssessmentDiscoveryWriter Writer() => new(CreateContext);

    internal async Task<Scan> SeedAsync(PageBaseTypeConfiguration configuration = null, int evidenceVersion = 1)
    {
        var scan = new Scan
        {
            ScanId = ScanId, CLIMode = "Classic", CLIAuthMode = "application",
            PublishingLayoutRuleVersion = 1, PublishingLayoutTypeCatalogJson = new PublishingLayoutTypeCatalog().ToJson(),
            PageSourceEvidenceVersion = evidenceVersion,
            PageBaseTypeConfigurationJson = evidenceVersion == 1 ? (configuration ?? new()).ToJson() : null,
        };
        using var db = CreateContext();
        db.Scans.Add(scan);
        await db.SaveChangesAsync();
        return scan;
    }

    internal ClassicPageDiscovery Page(string name = "original.aspx", Guid? id = null, int? item = 7)
    {
        var record = new RawDiscoveryRecord("synthetic-item", (id ?? FileId).ToString("D"), "synthetic-folder",
            name, Web + "/Forms/" + name, true, "synthetic-permission",
            new Dictionary<string, string> { ["original, \"metadata\""] = "raw\nvalue" }, SiteId, WebId,
            item.HasValue ? ListId : null, ListItemId: item, HomePage: false,
            ContentTypeId: AspxAssetPurpose.PublishingContentType, PageType: "PublishingPage",
            LibraryHidden: true, ObservationMethod: "SyntheticForms");
        var scope = new DiscoveryScopeRegistration("synthetic-folder", "synthetic-web", DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, Web + "/Forms", "synthetic-permission");
        var row = AssessmentWebDiscovery.Page(ScanId, Site, Web, scope, record, 1);
        row.ObservedAtUtc = DiscoveryTime.UtcDateTime;
        row.DiscoveryObservation = AspxFileObservation.FromDiscovery(row, record);
        return row;
    }

    internal static Task<AspxSourceReadResult> Capture(ClassicPageDiscovery row, byte[] bytes,
        AspxFileIdentity physical = null, AspxSourceVersion version = null, long? expected = null, int? httpStatus = null) =>
        AspxSourceReader.ReadAsync(row.DiscoveryObservation, physical ?? row.DiscoveryObservation.Identity,
            version ?? new(ReadTime, "\"synthetic, version\"", 3, 7),
            _ => Task.FromResult<Stream>(new MemoryStream(bytes)), default, expected ?? bytes.LongLength, httpStatus);

    internal async Task AcquireAsync(Scan scan, ClassicPageDiscovery row, AspxSourceReadResult read)
    {
        await AspxSourceAcquisition.ForAssessmentScan(scan, (_, _) => Task.FromResult(read))(row, default);
        await Writer().WriteAsync(new[] { row });
    }

    internal async Task<ClassicPageDiscovery> ReopenAsync(string recordKey)
    {
        using var db = CreateContext();
        return await db.ClassicPageDiscoveries.AsNoTracking().SingleAsync(row => row.ScanId == ScanId && row.RecordKey == recordKey);
    }

    internal async Task<IReadOnlyList<Dictionary<string, string>>> ExportAsync()
    {
        var path = Path.Combine(DirectoryPath, "report");
        Directory.CreateDirectory(path);
        using (var db = CreateContext())
            await ReportManager.ExportClassicReportDataAsync(db, ScanId, path, new CsvConfiguration(CultureInfo.InvariantCulture));
        return ReadIndependentCsv(Path.Combine(path, "discovery.csv")).Rows;
    }

    // Small independent RFC 4180 reader. Unlike line-based readers, it preserves CR and LF inside
    // quoted fields exactly. No product model/serializer generates expected headers or values.
    internal static (string[] Headers, List<Dictionary<string, string>> Rows) ReadIndependentCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var records = new List<string[]>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character != '"') field.Append(character);
                else if (index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; }
                else { quoted = false; closedQuote = true; }
            }
            else if (character == '"' && field.Length == 0 && !closedQuote) quoted = true;
            else if (character == ',' || character is '\r' or '\n')
            {
                record.Add(field.ToString()); field.Clear(); closedQuote = false;
                if (character != ',')
                {
                    records.Add(record.ToArray()); record.Clear();
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                }
            }
            else
            {
                if (closedQuote || character == '"') throw new InvalidDataException("Unexpected character outside a quoted CSV field.");
                field.Append(character);
            }
        }
        if (quoted) throw new InvalidDataException("Unterminated quoted CSV field.");
        if (record.Count != 0 || field.Length != 0 || closedQuote) { record.Add(field.ToString()); records.Add(record.ToArray()); }
        var headers = records.FirstOrDefault() ?? Array.Empty<string>();
        var rows = new List<Dictionary<string, string>>();
        foreach (var values in records.Skip(1))
        {
            if (values.Length != headers.Length) throw new InvalidDataException("CSV row width differs from its header.");
            rows.Add(headers.Zip(values).ToDictionary(value => value.First, value => value.Second, StringComparer.Ordinal));
        }
        return (headers, rows);
    }

    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
