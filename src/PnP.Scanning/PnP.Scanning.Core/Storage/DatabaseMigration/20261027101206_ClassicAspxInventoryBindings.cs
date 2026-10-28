using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicAspxInventoryBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassicAspxInventories",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AspxKey = table.Column<string>(type: "TEXT", nullable: false),
                    IdentityKind = table.Column<string>(type: "TEXT", nullable: false),
                    IdentityVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SiteCollectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FileUniqueId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WebId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    PageType = table.Column<string>(type: "TEXT", nullable: true),
                    ContentTypeId = table.Column<string>(type: "TEXT", nullable: true),
                    CustomizedPageStatus = table.Column<string>(type: "TEXT", nullable: true),
                    SetupPath = table.Column<string>(type: "TEXT", nullable: true),
                    FileReadState = table.Column<string>(type: "TEXT", nullable: false),
                    FileErrorCodes = table.Column<string>(type: "TEXT", nullable: true),
                    FileErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerDeclaredType = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerNormalizedType = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerEffectiveType = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerReadState = table.Column<string>(type: "TEXT", nullable: false),
                    HandlerParseState = table.Column<string>(type: "TEXT", nullable: false),
                    HandlerSource = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerDefaultProvenance = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerReasonCode = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerErrorCodes = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    CatalogSource = table.Column<string>(type: "TEXT", nullable: true),
                    CatalogVersion = table.Column<string>(type: "TEXT", nullable: true),
                    CatalogDigest = table.Column<string>(type: "TEXT", nullable: true),
                    CatalogEntryKey = table.Column<string>(type: "TEXT", nullable: true),
                    ProvenanceJson = table.Column<string>(type: "TEXT", nullable: false),
                    CoverageState = table.Column<string>(type: "TEXT", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicAspxInventories", x => new { x.ScanId, x.AspxKey });
                    table.CheckConstraint("CK_ClassicAspxInventories_Identity", "length(\"AspxKey\") > 0 AND \"IdentityVersion\" > 0 AND ((\"IdentityKind\" = 'TenantFile' AND \"SiteCollectionId\" IS NOT NULL AND \"SiteCollectionId\" <> '00000000-0000-0000-0000-000000000000' AND \"FileUniqueId\" IS NOT NULL AND \"FileUniqueId\" <> '00000000-0000-0000-0000-000000000000') OR (\"IdentityKind\" = 'PlatformCatalog' AND \"SiteCollectionId\" IS NULL AND \"FileUniqueId\" IS NULL AND \"CatalogSource\" IS NOT NULL AND \"CatalogVersion\" IS NOT NULL AND \"CatalogDigest\" IS NOT NULL AND \"CatalogEntryKey\" IS NOT NULL AND length(trim(\"CatalogSource\")) > 0 AND length(trim(\"CatalogVersion\")) > 0 AND length(\"CatalogDigest\") = 64 AND length(trim(\"CatalogEntryKey\")) > 0))");
                });

            migrationBuilder.CreateTable(
                name: "ClassicAspxItemBindings",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AspxKey = table.Column<string>(type: "TEXT", nullable: false),
                    SiteCollectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemGuid = table.Column<Guid>(type: "TEXT", nullable: true),
                    ItemGuidReadState = table.Column<string>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ItemUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ItemTitle = table.Column<string>(type: "TEXT", nullable: true),
                    ContentTypeId = table.Column<string>(type: "TEXT", nullable: true),
                    BindingKinds = table.Column<int>(type: "INTEGER", nullable: false),
                    BindingProvenanceJson = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicAspxItemBindings", x => new { x.ScanId, x.AspxKey, x.SiteCollectionId, x.WebId, x.ListId, x.ItemId });
                    table.CheckConstraint("CK_ClassicAspxItemBindings_RealItem", "\"SiteCollectionId\" <> '00000000-0000-0000-0000-000000000000' AND \"WebId\" <> '00000000-0000-0000-0000-000000000000' AND \"ListId\" <> '00000000-0000-0000-0000-000000000000' AND \"ItemId\" > 0 AND \"BindingKinds\" > 0 AND (\"BindingKinds\" & ~31) = 0");
                    table.ForeignKey(
                        name: "FK_ClassicAspxItemBindings_ClassicAspxInventories_ScanId_AspxKey",
                        columns: x => new { x.ScanId, x.AspxKey },
                        principalTable: "ClassicAspxInventories",
                        principalColumns: new[] { "ScanId", "AspxKey" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicAspxInventories_ScanId_CatalogSource_CatalogVersion_CatalogDigest_CatalogEntryKey",
                table: "ClassicAspxInventories",
                columns: new[] { "ScanId", "CatalogSource", "CatalogVersion", "CatalogDigest", "CatalogEntryKey" },
                unique: true,
                filter: "\"IdentityKind\" = 'PlatformCatalog'");

            migrationBuilder.CreateIndex(
                name: "IX_ClassicAspxInventories_ScanId_IdentityKind",
                table: "ClassicAspxInventories",
                columns: new[] { "ScanId", "IdentityKind" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicAspxInventories_ScanId_SiteCollectionId_FileUniqueId",
                table: "ClassicAspxInventories",
                columns: new[] { "ScanId", "SiteCollectionId", "FileUniqueId" },
                unique: true,
                filter: "\"IdentityKind\" = 'TenantFile'");

            migrationBuilder.CreateIndex(
                name: "IX_ClassicAspxItemBindings_ScanId_ItemGuid",
                table: "ClassicAspxItemBindings",
                columns: new[] { "ScanId", "ItemGuid" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicAspxItemBindings_ScanId_SiteCollectionId_WebId_ListId_ItemId",
                table: "ClassicAspxItemBindings",
                columns: new[] { "ScanId", "SiteCollectionId", "WebId", "ListId", "ItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicAspxItemBindings");

            migrationBuilder.DropTable(
                name: "ClassicAspxInventories");
        }
    }
}
