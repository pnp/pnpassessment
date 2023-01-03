using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class v150 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassicExtensibilities",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    UsesCustomMasterPage = table.Column<bool>(type: "INTEGER", nullable: false),
                    MasterPage = table.Column<string>(type: "TEXT", nullable: true),
                    CustomMasterPage = table.Column<string>(type: "TEXT", nullable: true),
                    UsesCustomCSS = table.Column<bool>(type: "INTEGER", nullable: false),
                    AlternateCSS = table.Column<string>(type: "TEXT", nullable: true),
                    UsesCustomTheme = table.Column<bool>(type: "INTEGER", nullable: false),
                    UsesUserCustomAction = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicExtensibilities", x => new { x.ScanId, x.SiteUrl, x.WebUrl });
                });

            migrationBuilder.CreateTable(
                name: "ClassicInfoPath",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ListTitle = table.Column<string>(type: "TEXT", nullable: true),
                    InfoPathUsage = table.Column<string>(type: "TEXT", nullable: true),
                    InfoPathTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastItemUserModifiedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicInfoPath", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.ListId });
                });

            migrationBuilder.CreateTable(
                name: "ClassicLists",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ListTitle = table.Column<string>(type: "TEXT", nullable: true),
                    ListTemplateType = table.Column<string>(type: "TEXT", nullable: true),
                    ListTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    ListExperience = table.Column<string>(type: "TEXT", nullable: true),
                    ClassicByDesign = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultViewRenderType = table.Column<string>(type: "TEXT", nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicLists", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.ListId });
                });

            migrationBuilder.CreateTable(
                name: "ClassicPages",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PageName = table.Column<string>(type: "TEXT", nullable: true),
                    PageType = table.Column<string>(type: "TEXT", nullable: true),
                    ListUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ListTitle = table.Column<string>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicPages", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.PageUrl });
                });

            migrationBuilder.CreateTable(
                name: "ClassicSiteCollections",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    RootWebTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    SubWebTemplates = table.Column<string>(type: "TEXT", nullable: true),
                    SubWebCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SubWebDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicLists = table.Column<int>(type: "INTEGER", nullable: false),
                    ModernLists = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicWikiPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicASPXPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicBlogPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicWebPartPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicPublishingPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ModernPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicWorkflows = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicInfoPathForms = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicExtensibilities = table.Column<int>(type: "INTEGER", nullable: false),
                    SharePointAddIns = table.Column<int>(type: "INTEGER", nullable: false),
                    AzureACSPrincipals = table.Column<int>(type: "INTEGER", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicSiteCollections", x => new { x.ScanId, x.SiteUrl });
                });

            migrationBuilder.CreateTable(
                name: "ClassicUserCustomActions",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    RegistrationType = table.Column<string>(type: "TEXT", nullable: true),
                    RegistrationId = table.Column<string>(type: "TEXT", nullable: true),
                    CommandAction = table.Column<string>(type: "TEXT", nullable: true),
                    CommandUIExtension = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptBlock = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptSrc = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ListTitle = table.Column<string>(type: "TEXT", nullable: true),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicUserCustomActions", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ClassicWebSummaries",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Template = table.Column<string>(type: "TEXT", nullable: true),
                    ClassicLists = table.Column<int>(type: "INTEGER", nullable: false),
                    ModernLists = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicWikiPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicASPXPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicBlogPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicWebPartPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassicPublishingPages = table.Column<int>(type: "INTEGER", nullable: false),
                    ModernPages = table.Column<int>(type: "INTEGER", nullable: false),
                    IsModernSite = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsClassicPublishingSite = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsModernCommunicationSite = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasClassicWorkflow = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClassicWorkflows = table.Column<int>(type: "INTEGER", nullable: false),
                    HasClassicInfoPathForms = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClassicInfoPathForms = table.Column<int>(type: "INTEGER", nullable: false),
                    HasClassicExtensibility = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClassicExtensibilities = table.Column<int>(type: "INTEGER", nullable: false),
                    HasSharePointAddIns = table.Column<bool>(type: "INTEGER", nullable: false),
                    SharePointAddIns = table.Column<int>(type: "INTEGER", nullable: false),
                    HasAzureACSPrincipal = table.Column<bool>(type: "INTEGER", nullable: false),
                    AzureACSPrincipals = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicWebSummaries", x => new { x.ScanId, x.SiteUrl, x.WebUrl });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicExtensibilities_ScanId_SiteUrl_WebUrl",
                table: "ClassicExtensibilities",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicInfoPath_ScanId_SiteUrl_WebUrl_ListId",
                table: "ClassicInfoPath",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "ListId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicLists_ScanId_SiteUrl_WebUrl_ListId",
                table: "ClassicLists",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "ListId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicPages_ScanId_SiteUrl_WebUrl_PageUrl",
                table: "ClassicPages",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "PageUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicSiteCollections_ScanId_SiteUrl",
                table: "ClassicSiteCollections",
                columns: new[] { "ScanId", "SiteUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicUserCustomActions_ScanId_SiteUrl_WebUrl_Id",
                table: "ClassicUserCustomActions",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicWebSummaries_ScanId_SiteUrl_WebUrl",
                table: "ClassicWebSummaries",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicExtensibilities");

            migrationBuilder.DropTable(
                name: "ClassicInfoPath");

            migrationBuilder.DropTable(
                name: "ClassicLists");

            migrationBuilder.DropTable(
                name: "ClassicPages");

            migrationBuilder.DropTable(
                name: "ClassicSiteCollections");

            migrationBuilder.DropTable(
                name: "ClassicUserCustomActions");

            migrationBuilder.DropTable(
                name: "ClassicWebSummaries");
        }
    }
}
