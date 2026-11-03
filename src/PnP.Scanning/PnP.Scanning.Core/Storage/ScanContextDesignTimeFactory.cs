using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PnP.Scanning.Core.Storage;

/// <summary>Creates an isolated tooling context without running the CLI, waiting for a debugger, or opening a scan database.</summary>
internal sealed class ScanContextDesignTimeFactory : IDesignTimeDbContextFactory<ScanContext>
{
    private const string ToolingConnection = "Data Source=:memory:";

    /// <summary>Uses the normal ScanContext model and migration assembly against a disposable in-memory SQLite connection.</summary>
    public ScanContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(ToolingConnection).Options;
        return new ScanContext(options);
    }
}
