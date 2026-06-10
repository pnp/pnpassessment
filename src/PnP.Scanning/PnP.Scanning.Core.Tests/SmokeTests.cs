using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests
{
    /// <summary>
    /// Proves the test harness + EF Core SQLite in-memory path work end-to-end. Everything
    /// else's unit tests build on this fixture.
    /// </summary>
    public class SmokeTests : IClassFixture<ScanContextFixture>
    {
        private readonly ScanContextFixture fixture;

        public SmokeTests(ScanContextFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public void Smoke_Fixture_CreatesDatabase()
        {
            using var context = fixture.CreateContext();

            // The migrated, in-memory database is reachable...
            context.Database.CanConnect().Should().BeTrue();

            // ...the product migrations were applied...
            context.Database.GetAppliedMigrations().Should().NotBeEmpty();

            // ...and a known table from the schema is queryable (and empty on a fresh DB).
            context.Scans.Count().Should().Be(0);
        }
    }
}
