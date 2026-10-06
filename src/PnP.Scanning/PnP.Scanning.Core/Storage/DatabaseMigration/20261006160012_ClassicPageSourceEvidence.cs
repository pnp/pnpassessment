using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicPageSourceEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PageBaseTypeConfigurationJson",
                table: "Scans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PageSourceEvidenceVersion",
                table: "Scans",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceEvidenceJson",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PageBaseTypeConfigurationJson",
                table: "Scans");

            migrationBuilder.DropColumn(
                name: "PageSourceEvidenceVersion",
                table: "Scans");

            migrationBuilder.DropColumn(
                name: "SourceEvidenceJson",
                table: "ClassicPageDiscoveries");
        }
    }
}
