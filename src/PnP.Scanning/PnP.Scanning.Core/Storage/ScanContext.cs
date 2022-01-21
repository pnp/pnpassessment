using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace PnP.Scanning.Core.Storage
{
    internal class ScanContext : DbContext
    {
        internal DbSet<Scan>? Scans { get; set; }

        internal DbSet<Property>? Properties { get; set; }

        internal DbSet<SiteCollection>? SiteCollections { get; set; }

        internal DbSet<Web>? Webs { get; set; }

#if DEBUG
        internal DbSet<TestDelay>? TestDelays { get; set; }
#endif

        internal string DbPath { get; }

        internal ScanContext(Guid scanId)
        {
            var path = StorageManager.GetScanDataFolder(scanId);

            // Ensure path exists
            Directory.CreateDirectory(path);

            DbPath = Path.Join(path, "scan.db");
        }

        // The following configures EF to create a Sqlite database file
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        { 
            optionsBuilder.UseSqlite($"Data Source={DbPath}", options =>
            {
                options.MigrationsAssembly(Assembly.GetExecutingAssembly().FullName);
            });

            base.OnConfiguring(optionsBuilder);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {

            modelBuilder.Entity<Scan>().ToTable("Scans");
            modelBuilder.Entity<Scan>(entity =>
            {
                entity.HasKey(e => e.ScanId);                
            });

            modelBuilder.Entity<Property>().ToTable("Properties");
            modelBuilder.Entity<Property>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.Name });
                entity.HasIndex(e => new { e.ScanId, e.Name }).IsUnique();
            });

            modelBuilder.Entity<SiteCollection>().ToTable("SiteCollections");
            modelBuilder.Entity<SiteCollection>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl });
                entity.HasIndex(e => new { e.ScanId, e.SiteUrl }).IsUnique();
                entity.HasIndex(e => e.Status);
            });

            modelBuilder.Entity<Web>().ToTable("Webs");
            modelBuilder.Entity<Web>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl });
                entity.HasIndex(e => new { e.ScanId, e.SiteUrl, e.WebUrl }).IsUnique();
                entity.HasIndex(e => e.Status);
            });

#if DEBUG
            modelBuilder.Entity<TestDelay>().ToTable("TestDelays");
            modelBuilder.Entity<TestDelay>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl });
                entity.HasIndex(e => new { e.ScanId, e.SiteUrl, e.WebUrl });
            });
            base.OnModelCreating(modelBuilder);
#endif
        }
    }
}
