using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1111 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassicWebParts",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebPartId = table.Column<string>(type: "TEXT", nullable: false),
                    PageName = table.Column<string>(type: "TEXT", nullable: true),
                    WebPartType = table.Column<string>(type: "TEXT", nullable: true),
                    WebPartTitle = table.Column<string>(type: "TEXT", nullable: true),
                    WebPartZone = table.Column<int>(type: "INTEGER", nullable: false),
                    WebPartZoneIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    WebPartProperties = table.Column<string>(type: "TEXT", nullable: true),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    WebPartAssembly = table.Column<string>(type: "TEXT", nullable: true),
                    WebPartClass = table.Column<string>(type: "TEXT", nullable: true),
                    HasProperMapping = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemediationCode = table.Column<string>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicWebParts", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.PageUrl, x.WebPartId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicWebParts_ScanId_SiteUrl_WebUrl_PageUrl_WebPartId",
                table: "ClassicWebParts",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "PageUrl", "WebPartId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicWebParts");
        }
    }
}
