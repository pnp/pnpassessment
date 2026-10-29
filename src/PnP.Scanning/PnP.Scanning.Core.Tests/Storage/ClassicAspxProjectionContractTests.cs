#nullable enable
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class ClassicAspxProjectionContractTests : IClassFixture<ScanContextFixture>
{
    private const string LegacyMarkerName = "M1ProjectionContract";
    private const string LegacyScopeName = "M1ScopeMode";
    private const string LegacyVersion = "M1/v1";
    private readonly ScanContextFixture fixture;

    /// <summary>Isolates each stored declaration case by scan ID in the shared migrated fixture.</summary>
    public ClassicAspxProjectionContractTests(ScanContextFixture fixture)
    {
        this.fixture = fixture;
    }

    /// <summary>Normalizes both known property families without altering or backfilling the saved declaration.</summary>
    [Theory]
    [InlineData(false, "DeclaredFullInventory")]
    [InlineData(false, "HomePageOnly")]
    [InlineData(true, "DeclaredFullInventory")]
    [InlineData(true, "HomePageOnly")]
    public async Task SupportedDeclarationsNormalizeWithoutWrites(bool legacy, string scope)
    {
        var scanId = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Properties.AddRange(
            new Property { ScanId = scanId, Name = legacy ? LegacyMarkerName : "AspxProjectionContract", Value = legacy ? LegacyVersion : "aspx-inventory/v1" },
            new Property { ScanId = scanId, Name = legacy ? LegacyScopeName : "AspxScopeMode", Value = scope });
        await context.SaveChangesAsync();
        var before = await context.Properties.AsNoTracking().Where(row => row.ScanId == scanId)
            .OrderBy(row => row.Name).Select(row => new { row.Name, row.Value }).ToListAsync();

        var result = await ClassicAspxProjectionContract.ReadAsync(context, scanId);

        result.IsRecognized.Should().BeTrue();
        result.Version.Should().Be("aspx-inventory/v1");
        result.ScopeMode.Should().Be(scope);
        var after = await context.Properties.AsNoTracking().Where(row => row.ScanId == scanId)
            .OrderBy(row => row.Name).Select(row => new { row.Name, row.Value }).ToListAsync();
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    /// <summary>Never borrows an old version or scope when any current-family declaration is present.</summary>
    [Theory]
    [InlineData("unknown-v2", null, false)]
    [InlineData("M1/v1", null, false)]
    [InlineData(null, "HomePageOnly", false)]
    [InlineData("aspx-inventory/v1", null, true)]
    [InlineData("aspx-inventory/v1", "HomePageOnly", true)]
    public async Task CurrentFamilyCannotBorrowLegacyAuthority(string? version, string? scope, bool recognized)
    {
        var scanId = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Properties.AddRange(
            new Property { ScanId = scanId, Name = LegacyMarkerName, Value = LegacyVersion },
            new Property { ScanId = scanId, Name = LegacyScopeName, Value = "DeclaredFullInventory" });
        if (version != null)
        {
            context.Properties.Add(new Property { ScanId = scanId, Name = "AspxProjectionContract", Value = version });
        }
        if (scope != null)
        {
            context.Properties.Add(new Property { ScanId = scanId, Name = "AspxScopeMode", Value = scope });
        }
        await context.SaveChangesAsync();

        var result = await ClassicAspxProjectionContract.ReadAsync(context, scanId);

        result.IsRecognized.Should().Be(recognized);
        result.Version.Should().Be(recognized ? "aspx-inventory/v1" : null);
        result.ScopeMode.Should().Be(scope);
    }

    /// <summary>Does not turn an upgraded schema with no saved marker into proof of completed acquisition.</summary>
    [Fact]
    public async Task MissingDeclarationRemainsUnknown()
    {
        using var context = fixture.CreateContext();
        var result = await ClassicAspxProjectionContract.ReadAsync(context, Guid.NewGuid());
        result.IsRecognized.Should().BeFalse();
        result.Version.Should().BeNull();
        result.ScopeMode.Should().BeNull();
    }
    /// <summary>Writes only neutral contract properties for Pages, preserving full/home intent and excluding unrelated components.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void NewStartRequestsUseDomainDeclarationOnly(bool pages, bool homePageOnly)
    {
        var request = new PnP.Scanning.Core.Services.StartRequest();
        var components = pages
            ? new[] { PnP.Scanning.Core.Services.ClassicComponent.Pages }
            : Array.Empty<PnP.Scanning.Core.Services.ClassicComponent>();
        PnP.Scanning.Core.Scanners.ClassicStartRequestBuilder.AddClassicProperties(
            request, components, false, false, false, homePageOnly);

        request.Properties.Should().NotContain(property => property.Property.StartsWith("M1", StringComparison.OrdinalIgnoreCase));
        if (pages)
        {
            request.Properties.Should().ContainSingle(property =>
                property.Property == "AspxProjectionContract" && property.Value == "aspx-inventory/v1");
            request.Properties.Should().ContainSingle(property =>
                property.Property == "AspxScopeMode" && property.Value == (homePageOnly ? "HomePageOnly" : "DeclaredFullInventory"));
        }
        else
        {
            request.Properties.Should().NotContain(property => property.Property == "AspxProjectionContract" || property.Property == "AspxScopeMode");
        }
    }

}
