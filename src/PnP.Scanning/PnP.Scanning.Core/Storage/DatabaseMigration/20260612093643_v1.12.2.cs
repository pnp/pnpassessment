using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1122 : Migration
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
