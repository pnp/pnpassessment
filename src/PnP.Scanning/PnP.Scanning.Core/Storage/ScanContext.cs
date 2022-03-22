using Microsoft.EntityFrameworkCore;
using System.Reflection;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    /// <summary>
    /// Scan database context used to work with the database
    /// 
    /// Note: For each new scan component work is needed here. Check the PER SCAN COMPONENT: strings to find the right places to add code
    /// </summary>
    internal class ScanContext : DbContext
    {        

        internal DbSet<Scan> Scans { get; set; }

        internal DbSet<Property> Properties { get; set; }

        internal DbSet<History> History { get; set; }

        internal DbSet<Cache> Cache { get; set; } 

        internal DbSet<SiteCollection> SiteCollections { get; set; }

        internal DbSet<Web> Webs { get; set; }

        // PER SCAN COMPONENT: add new tables needed to store the data for the scan component
        internal DbSet<SyntexList> SyntexLists { get; set; }

        internal DbSet<SyntexContentType> SyntexContentTypes { get; set; }

        internal DbSet<SyntexContentTypeSummary> SyntexContentTypeOverview { get; set; }

        internal DbSet<SyntexContentTypeField> SyntexContentTypeFields { get; set; }

        internal DbSet<SyntexField> SyntexFields { get; set; }

        internal DbSet<SyntexModelUsage> SyntexModelUsage { get; set; }

        internal DbSet<Workflow> Workflows { get; set; }

#if DEBUG
        internal DbSet<TestDelay> TestDelays { get; set; }
#endif

        internal string DbPath { get; }

        /// <summary>
        /// Parameterless constructor used for EF Design time operations
        /// </summary>
        public ScanContext()
        {
        }

        internal ScanContext(Guid scanId)
        {
            var path = StorageManager.GetScanDataFolder(scanId);

            // Ensure path exists
            Directory.CreateDirectory(path);

            DbPath = Path.Join(path, StorageManager.DbName);
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
            // Extend the model defined via annotations, typically used for defining compound keys

            modelBuilder.Entity<Property>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.Name });
            });

            modelBuilder.Entity<Cache>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.Key });
            });

            modelBuilder.Entity<SiteCollection>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl });
            });

            modelBuilder.Entity<Web>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl });
            });

            // PER SCAN COMPONENT: define needed tables here
            #region Syntex scanner
            modelBuilder.Entity<SyntexList>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.ListId });
            });

            modelBuilder.Entity<SyntexContentType>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.ListId, e.ContentTypeId });
            });

            modelBuilder.Entity<SyntexContentTypeSummary>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.ContentTypeId });
            });

            modelBuilder.Entity<SyntexContentTypeField>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.ListId, e.ContentTypeId, e.FieldId });
            });

            modelBuilder.Entity<SyntexField>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.ListId, e.FieldId });
            });

            modelBuilder.Entity<SyntexModelUsage>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.Classifier, e.TargetSiteId, e.TargetWebId, e.TargetListId });
            });

            modelBuilder.Entity<Workflow>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.DefinitionId, e.SubscriptionId });
            });
            #endregion

#if DEBUG
            modelBuilder.Entity<TestDelay>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl });
            });
#endif

            base.OnModelCreating(modelBuilder);
        }
    }
}
