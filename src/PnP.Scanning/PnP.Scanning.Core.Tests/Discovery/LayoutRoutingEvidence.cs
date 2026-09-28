using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using static PnP.Scanning.Core.Tests.Discovery.PublishingLayoutTypeEvidenceTests;

namespace PnP.Scanning.Core.Tests.Discovery;

internal static class LayoutRoutingEvidence
{
    internal static async Task RecordAuthorityAsync(ScanContext db, Guid scan, int version = 1)
    {
        db.Scans.Add(new Scan { ScanId = scan, PublishingLayoutRuleVersion = version });
        await db.SaveChangesAsync();
    }

    internal static async Task InspectAsync(ClassicPageDiscovery row, string type = "Root", string body = "")
    {
        using var assembly = new MetadataFixture();
        var catalog = PublishingLayoutTypeCatalog.Capture(new[] { assembly.Path });
        var identity = type == "Root" ? Root : type == "TemplateRedirectionPage"
            ? Root.Replace("PublishingLayoutPage", "TemplateRedirectionPage") : Identity(type);
        await PublishingLayoutTypeEvidence.AcquireAsync(row,
            (_, _) => Task.FromResult(type == null ? null : Source(identity) + body), catalog, default);
    }
}
