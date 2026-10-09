using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Process.Storage;

/// <summary>EF tooling needs only the model and must not launch the CLI or initialize authentication.</summary>
internal sealed class ScanContextFactory : IDesignTimeDbContextFactory<ScanContext>
{
    public ScanContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ScanContext>()
        .UseSqlite("Data Source=:memory:").Options);
}
