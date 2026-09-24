using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicAssetPurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssetPurpose",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "AssetPurposeReason",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "NotEvaluated");

            migrationBuilder.AddColumn<string>(
                name: "AssetPurposeStatus",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssetPurpose",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "AssetPurposeReason",
                table: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "AssetPurposeStatus",
                table: "ClassicPageDiscoveries");
        }
    }
}
