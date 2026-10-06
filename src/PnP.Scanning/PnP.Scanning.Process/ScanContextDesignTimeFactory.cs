using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Process;

/// <summary>
/// Offline EF scaffolding/script generation. Avoid running the Process CLI, including its
/// startup version check. Database update requires an explicit --connection for a durable file.
/// The existing Core project, Process startup project and migration output convention are unchanged.
/// </summary>
internal sealed class ScanContextDesignTimeFactory : IDesignTimeDbContextFactory<ScanContext>
{
    public ScanContextDesignTimeFactory() { }

    public ScanContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ScanContext>().UseSqlite("Data Source=:memory:").Options);
}
