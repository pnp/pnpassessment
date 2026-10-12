using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Process.Storage;

/// <summary>
/// Supplies an assessment EF model for design-time commands without launching the CLI or initializing authentication.
/// </summary>
internal sealed class ScanContextFactory : IDesignTimeDbContextFactory<ScanContext>
{
    /// <summary>
    /// Creates the SQLite context needed by EF migration tooling with an in-memory connection.
    /// </summary>
    public ScanContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ScanContext>()
        .UseSqlite("Data Source=:memory:").Options);
}
