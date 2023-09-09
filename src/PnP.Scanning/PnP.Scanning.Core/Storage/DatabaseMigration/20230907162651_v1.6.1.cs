using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class v161 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassicACSPrincipals",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    HasExpired = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasTenantScopedPermissions = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasSiteCollectionScopedPermissions = table.Column<bool>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    AllowAppOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RedirectUri = table.Column<string>(type: "TEXT", nullable: true),
                    AppDomains = table.Column<string>(type: "TEXT", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicACSPrincipals", x => new { x.ScanId, x.AppIdentifier });
                });

            migrationBuilder.CreateTable(
                name: "classicACSPrincipalSites",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    ServerRelativeUrl = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classicACSPrincipalSites", x => new { x.ScanId, x.AppIdentifier, x.ServerRelativeUrl });
                });

            migrationBuilder.CreateTable(
                name: "ClassicACSPrincipalSiteScopedPermissions",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    ServerRelativeUrl = table.Column<string>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Right = table.Column<string>(type: "TEXT", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicACSPrincipalSiteScopedPermissions", x => new { x.ScanId, x.AppIdentifier, x.ServerRelativeUrl, x.SiteId, x.WebId, x.ListId, x.Right });
                });

            migrationBuilder.CreateTable(
                name: "ClassicACSPrincipalTenantScopedPermissions",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    ProductFeature = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    Right = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicACSPrincipalTenantScopedPermissions", x => new { x.ScanId, x.AppIdentifier, x.ProductFeature, x.Scope, x.Right, x.ResourceId });
                });

            migrationBuilder.CreateTable(
                name: "ClassicAddIns",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    AppInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    HasExpired = table.Column<bool>(type: "INTEGER", nullable: false),
                    AppSource = table.Column<string>(type: "TEXT", nullable: true),
                    AppWebFullUrl = table.Column<string>(type: "TEXT", nullable: true),
                    AppWebId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<string>(type: "TEXT", nullable: true),
                    CreationTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InstalledBy = table.Column<string>(type: "TEXT", nullable: true),
                    InstalledSiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstalledWebId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstalledWebFullUrl = table.Column<string>(type: "TEXT", nullable: true),
                    InstalledWebName = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentSiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CurrentWebId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CurrentWebFullUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentWebName = table.Column<string>(type: "TEXT", nullable: true),
                    LaunchUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LicensePurchaseTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PurchaserIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    Locale = table.Column<string>(type: "TEXT", nullable: true),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    TenantAppData = table.Column<string>(type: "TEXT", nullable: true),
                    TenantAppDataUpdateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicAddIns", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.AppInstanceId });
                });

            migrationBuilder.CreateTable(
                name: "TempClassicACSPrincipals",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    ServerRelativeUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    AllowAppOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RedirectUri = table.Column<string>(type: "TEXT", nullable: true),
                    AppDomains = table.Column<string>(type: "TEXT", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TempClassicACSPrincipals", x => new { x.ScanId, x.AppIdentifier, x.ServerRelativeUrl });
                });

            migrationBuilder.CreateTable(
                name: "TempClassicACSPrincipalValidUntils",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TempClassicACSPrincipalValidUntils", x => new { x.ScanId, x.AppIdentifier });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicACSPrincipals_ScanId_AppIdentifier",
                table: "ClassicACSPrincipals",
                columns: new[] { "ScanId", "AppIdentifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_classicACSPrincipalSites_ScanId_AppIdentifier_ServerRelativeUrl",
                table: "classicACSPrincipalSites",
                columns: new[] { "ScanId", "AppIdentifier", "ServerRelativeUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicACSPrincipalSiteScopedPermissions_ScanId_AppIdentifier_ServerRelativeUrl_SiteId_WebId_ListId_Right",
                table: "ClassicACSPrincipalSiteScopedPermissions",
                columns: new[] { "ScanId", "AppIdentifier", "ServerRelativeUrl", "SiteId", "WebId", "ListId", "Right" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicACSPrincipalTenantScopedPermissions_ScanId_AppIdentifier_ProductFeature_Scope_Right_ResourceId",
                table: "ClassicACSPrincipalTenantScopedPermissions",
                columns: new[] { "ScanId", "AppIdentifier", "ProductFeature", "Scope", "Right", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicAddIns_ScanId_SiteUrl_WebUrl_AppInstanceId",
                table: "ClassicAddIns",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "AppInstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TempClassicACSPrincipals_ScanId_AppIdentifier_ServerRelativeUrl",
                table: "TempClassicACSPrincipals",
                columns: new[] { "ScanId", "AppIdentifier", "ServerRelativeUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TempClassicACSPrincipalValidUntils_ScanId_AppIdentifier",
                table: "TempClassicACSPrincipalValidUntils",
                columns: new[] { "ScanId", "AppIdentifier" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicACSPrincipals");

            migrationBuilder.DropTable(
                name: "classicACSPrincipalSites");

            migrationBuilder.DropTable(
                name: "ClassicACSPrincipalSiteScopedPermissions");

            migrationBuilder.DropTable(
                name: "ClassicACSPrincipalTenantScopedPermissions");

            migrationBuilder.DropTable(
                name: "ClassicAddIns");

            migrationBuilder.DropTable(
                name: "TempClassicACSPrincipals");

            migrationBuilder.DropTable(
                name: "TempClassicACSPrincipalValidUntils");
        }
    }
}
