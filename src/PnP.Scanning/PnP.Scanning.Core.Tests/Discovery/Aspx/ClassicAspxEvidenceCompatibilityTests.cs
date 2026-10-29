using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

public sealed class ClassicAspxEvidenceCompatibilityTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture fixture;

    /// <summary>Uses the shared migrated fixture with a fresh scan identity for every evidence-reconciliation case.</summary>
    public ClassicAspxEvidenceCompatibilityTests(ScanContextFixture fixture)
    {
        this.fixture = fixture;
    }

    /// <summary>Reuses the denied historical scope identity instead of adding a renamed successful duplicate during resume.</summary>
    [Fact]
    public async Task ResumedScopeKeepsHistoricalIdentityAndDeniedCoverage()
    {
        var scanId = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(() => fixture.CreateContext());
        await writer.WriteAsync(new[] { new ClassicPageDiscovery
        {
            ScanId = scanId, RecordKey = "m1:Scope:known", ParentScopeKey = "m1:web:known",
            RowType = "Scope", ScopeType = "Surface", DiscoveryStatus = "Denied", ErrorCodes = "HTTP403", EvidenceJson = "old-scope-proof"
        }});
        await writer.WriteAsync(new[] { new ClassicPageDiscovery
        {
            ScanId = scanId, RecordKey = "aspx:Scope:known", ParentScopeKey = "aspx:web:known",
            RowType = "Scope", ScopeType = "Surface", DiscoveryStatus = "Complete", EvidenceJson = "new-scope-proof"
        }});
        using var context = fixture.CreateContext();
        var row = await context.ClassicPageDiscoveries.SingleAsync(item => item.ScanId == scanId);
        row.RecordKey.Should().Be("m1:Scope:known");
        row.ParentScopeKey.Should().Be("m1:web:known");
        row.DiscoveryStatus.Should().Be("Denied");
        row.EvidenceJson.Should().Be("old-scope-proof");
        row.ErrorCodes.Should().Contain("HTTP403");
    }

    /// <summary>Retains old pagination receipt bytes and explicit uncertainty when resumed evidence has changed.</summary>
    [Fact]
    public async Task ResumedPaginationRetainsReceiptWithoutCertifyingChangedEvidence()
    {
        var scanId = Guid.NewGuid();
        const string oldReceipt = "{\"authorityRevision\":\"M1/v1\",\"pageOrdinal\":0}";
        var writer = new AssessmentDiscoveryWriter(() => fixture.CreateContext());
        await writer.WriteAsync(new[] { new ClassicPageDiscovery
        {
            ScanId = scanId, RecordKey = "m1:Pagination:known", RowType = "Pagination",
            DiscoveryStatus = "Complete", EvidenceJson = oldReceipt
        }});
        await writer.WriteAsync(new[] { new ClassicPageDiscovery
        {
            ScanId = scanId, RecordKey = "aspx:Pagination:known", RowType = "Pagination",
            DiscoveryStatus = "Complete", EvidenceJson = "{\"authorityRevision\":\"aspx-inventory/v1\",\"pageOrdinal\":0}"
        }});
        using var context = fixture.CreateContext();
        var row = await context.ClassicPageDiscoveries.SingleAsync(item => item.ScanId == scanId);
        row.RecordKey.Should().Be("m1:Pagination:known");
        row.EvidenceJson.Should().Be(oldReceipt);
        row.DiscoveryStatus.Should().Be("Unknown");
        row.ErrorCodes.Should().Contain("ChangedDuringScan");
    }

    /// <summary>Stores only the neutral evidence identity when no earlier preview row exists in this scan.</summary>
    [Fact]
    public async Task NewScanWritesNeutralKeysOnly()
    {
        var scanId = Guid.NewGuid();
        await new AssessmentDiscoveryWriter(() => fixture.CreateContext()).WriteAsync(new[] { new ClassicPageDiscovery
        {
            ScanId = scanId, RecordKey = "aspx:Scope:new", RowType = "Scope", DiscoveryStatus = "Complete"
        }});
        using var context = fixture.CreateContext();
        var row = await context.ClassicPageDiscoveries.SingleAsync(item => item.ScanId == scanId);
        row.RecordKey.Should().Be("aspx:Scope:new");
    }
}
