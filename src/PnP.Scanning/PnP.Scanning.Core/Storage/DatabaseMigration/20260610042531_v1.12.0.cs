using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1120 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AvgMappingPercentage",
                table: "ClassicWebSummaries",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "MappableWebPartPages",
                table: "ClassicWebSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PagesWithWebParts",
                table: "ClassicWebSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UncustomizedHomePages",
                table: "ClassicWebSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UnmappedWebPartPages",
                table: "ClassicWebSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "AvgMappingPercentage",
                table: "ClassicSiteSummaries",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "MappableWebPartPages",
                table: "ClassicSiteSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PagesWithWebParts",
                table: "ClassicSiteSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UncustomizedHomePages",
                table: "ClassicSiteSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UnmappedWebPartPages",
                table: "ClassicSiteSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HomePage",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Layout",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UncustomizedHomePage",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ViewsLifeTime",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ViewsLifeTimeUniqueUsers",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ViewsRecent",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ViewsRecentUniqueUsers",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ClassicPageWebParts",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebPartIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    WebPartType = table.Column<string>(type: "TEXT", nullable: true),
                    WebPartTypeShort = table.Column<string>(type: "TEXT", nullable: true),
                    WebPartTitle = table.Column<string>(type: "TEXT", nullable: true),
                    WebPartProperties = table.Column<string>(type: "TEXT", nullable: true),
                    ZoneId = table.Column<string>(type: "TEXT", nullable: true),
                    Row = table.Column<int>(type: "INTEGER", nullable: false),
                    Column = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsMappable = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicPageWebParts", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.PageUrl, x.WebPartIndex });
                });

            migrationBuilder.CreateTable(
                name: "ClassicWebPartUniques",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebPartType = table.Column<string>(type: "TEXT", nullable: false),
                    InMappingFile = table.Column<bool>(type: "INTEGER", nullable: false),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicWebPartUniques", x => new { x.ScanId, x.WebPartType });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicPageWebParts_ScanId_SiteUrl_WebUrl_PageUrl_WebPartIndex",
                table: "ClassicPageWebParts",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "PageUrl", "WebPartIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassicWebPartUniques_ScanId_WebPartType",
                table: "ClassicWebPartUniques",
                columns: new[] { "ScanId", "WebPartType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicPageWebParts");

            migrationBuilder.DropTable(
                name: "ClassicWebPartUniques");

            migrationBuilder.DropColumn(
                name: "AvgMappingPercentage",
                table: "ClassicWebSummaries");

            migrationBuilder.DropColumn(
                name: "MappableWebPartPages",
                table: "ClassicWebSummaries");

            migrationBuilder.DropColumn(
                name: "PagesWithWebParts",
                table: "ClassicWebSummaries");

            migrationBuilder.DropColumn(
                name: "UncustomizedHomePages",
                table: "ClassicWebSummaries");

            migrationBuilder.DropColumn(
                name: "UnmappedWebPartPages",
                table: "ClassicWebSummaries");

            migrationBuilder.DropColumn(
                name: "AvgMappingPercentage",
                table: "ClassicSiteSummaries");

            migrationBuilder.DropColumn(
                name: "MappableWebPartPages",
                table: "ClassicSiteSummaries");

            migrationBuilder.DropColumn(
                name: "PagesWithWebParts",
                table: "ClassicSiteSummaries");

            migrationBuilder.DropColumn(
                name: "UncustomizedHomePages",
                table: "ClassicSiteSummaries");

            migrationBuilder.DropColumn(
                name: "UnmappedWebPartPages",
                table: "ClassicSiteSummaries");

            migrationBuilder.DropColumn(
                name: "HomePage",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "Layout",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "UncustomizedHomePage",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "ViewsLifeTime",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "ViewsLifeTimeUniqueUsers",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "ViewsRecent",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "ViewsRecentUniqueUsers",
                table: "ClassicPages");
        }
    }
}
