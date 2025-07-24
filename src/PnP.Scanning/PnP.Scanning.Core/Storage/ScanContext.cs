﻿using Microsoft.EntityFrameworkCore;
using System.Reflection;

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

        internal DbSet<SyntexFileType> SyntexFileTypes { get; set; }

        internal DbSet<SyntexTermSet> SyntexTermSets { get; set; }

        internal DbSet<Workflow> Workflows { get; set; }

        internal DbSet<ClassicInfoPath> ClassicInfoPath { get; set; }

        internal DbSet<ClassicPage> ClassicPages { get; set; }

        internal DbSet<ClassicWebPart> ClassicWebParts { get; set; }

        internal DbSet<ClassicList> ClassicLists { get; set; }

        internal DbSet<ClassicUserCustomAction> ClassicUserCustomActions { get; set; }

        internal DbSet<ClassicExtensibility> ClassicExtensibilities { get; set; }

        internal DbSet<ClassicSiteSummary> ClassicSiteSummaries { get; set; }

        internal DbSet<ClassicWebSummary> ClassicWebSummaries { get; set; }      

        internal DbSet<ClassicAddIn> ClassicAddIns { get; set; }

        internal DbSet<ClassicACSPrincipal> ClassicACSPrincipals { get; set; }

        internal DbSet<ClassicACSPrincipalSite> ClassicACSPrincipalSites { get; set; }

        internal DbSet<ClassicACSPrincipalSiteScopedPermissions> ClassicACSPrincipalSiteScopedPermissions { get; set; }

        internal DbSet<ClassicACSPrincipalTenantScopedPermissions> ClassicACSPrincipalTenantScopedPermissions { get; set; }

        internal DbSet<TempClassicACSPrincipal> TempClassicACSPrincipals { get; set; }

        internal DbSet<TempClassicACSPrincipalValidUntil> TempClassicACSPrincipalValidUntils { get; set; }

        internal DbSet<Alerts> Alerts { get; set; }

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

            modelBuilder.Entity<SyntexFileType>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.ListId, e.FileType });
            });

            modelBuilder.Entity<SyntexTermSet>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.TermSetId });
            });
            #endregion

            #region Workflow scanner
            modelBuilder.Entity<Workflow>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.DefinitionId, e.SubscriptionId });
            });
            #endregion

            #region InfoPath scanner
            modelBuilder.Entity<ClassicInfoPath>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.ListId });
            });
            #endregion

            #region Classic scanner
            modelBuilder.Entity<ClassicPage>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.PageUrl });
            });

            modelBuilder.Entity<ClassicWebPart>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.PageUrl, e.WebPartId });
            });

            modelBuilder.Entity<ClassicList>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.ListId });
            });

            modelBuilder.Entity<ClassicUserCustomAction>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.Id });
            });

            modelBuilder.Entity<ClassicExtensibility>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl });
            });

            modelBuilder.Entity<ClassicSiteSummary>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl });
            });

            modelBuilder.Entity<ClassicWebSummary>(entity =>
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl });
            });
            #endregion

            #region AddIn and ACS scanner

            modelBuilder.Entity<ClassicAddIn>(entity => 
            {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.AppInstanceId });
            });

            modelBuilder.Entity<TempClassicACSPrincipal>(entity => {
                entity.HasKey(e => new { e.ScanId, e.AppIdentifier, e.ServerRelativeUrl });
            });

            modelBuilder.Entity<ClassicACSPrincipal>(entity => {
                entity.HasKey(e => new { e.ScanId, e.AppIdentifier });
            });

            modelBuilder.Entity<ClassicACSPrincipalSite>(entity => {
                entity.HasKey(e => new { e.ScanId, e.AppIdentifier, e.ServerRelativeUrl });
            });

            modelBuilder.Entity<ClassicACSPrincipalSiteScopedPermissions>(entity => {
                entity.HasKey(e => new { e.ScanId, e.AppIdentifier, e.ServerRelativeUrl, e.SiteId, e.WebId, e.ListId, e.Right });
            });

            modelBuilder.Entity<ClassicACSPrincipalTenantScopedPermissions>(entity => {
                entity.HasKey(e => new { e.ScanId, e.AppIdentifier, e.ProductFeature, e.Scope, e.Right, e.ResourceId });
            });

            modelBuilder.Entity<TempClassicACSPrincipalValidUntil>(entity => {
                entity.HasKey(e => new { e.ScanId, e.AppIdentifier });
            });
            #endregion

            #region Alerts scanner
            modelBuilder.Entity<Alerts>(entity => {
                entity.HasKey(e => new { e.ScanId, e.SiteUrl, e.WebUrl, e.AlertId });
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
