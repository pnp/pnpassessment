using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1123 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassicPublishingSiteSummaries",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    NumberOfWebs = table.Column<int>(type: "INTEGER", nullable: false),
                    NumberOfPages = table.Column<int>(type: "INTEGER", nullable: false),
                    UsedSiteMasterPages = table.Column<string>(type: "TEXT", nullable: true),
                    UsedSystemMasterPages = table.Column<string>(type: "TEXT", nullable: true),
                    UsedPageLayouts = table.Column<string>(type: "TEXT", nullable: true),
                    LastPageUpdateDate = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicPublishingSiteSummaries", x => new { x.ScanId, x.SiteUrl });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicPublishingSiteSummaries_ScanId_SiteUrl",
                table: "ClassicPublishingSiteSummaries",
                columns: new[] { "ScanId", "SiteUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicPublishingSiteSummaries");
        }
    }
}
