using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicPublishingLayoutTypeEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PublishingLayoutRuleVersion",
                table: "Scans",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PublishingLayoutTypeCatalogJson",
                table: "Scans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeclaredPageType",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageTypeEvidenceJson",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageTypeEvidenceOrigin",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "PageTypeReason",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "NotEvaluated");

            migrationBuilder.AddColumn<string>(
                name: "PageTypeResolutionStatus",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "PageTypeSourceStatus",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "PublishingLayoutFamily",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "ResolvedPageType",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishingLayoutRuleVersion",
                table: "Scans");

            migrationBuilder.DropColumn(
                name: "PublishingLayoutTypeCatalogJson",
                table: "Scans");

            migrationBuilder.DropColumn(
                name: "DeclaredPageType",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "PageTypeEvidenceJson",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "PageTypeEvidenceOrigin",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "PageTypeReason",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "PageTypeResolutionStatus",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "PageTypeSourceStatus",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "PublishingLayoutFamily",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "ResolvedPageType",
                table: "ClassicPageDiscoveries");
        }
    }
}
