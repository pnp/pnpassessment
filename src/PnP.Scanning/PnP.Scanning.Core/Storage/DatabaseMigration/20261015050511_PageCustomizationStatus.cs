using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class PageCustomizationStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PageCustomizedPageStatus",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageLayoutCustomizedPageStatus",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageLayoutUrl",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageCustomizedPageStatus",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PageCustomizedPageStatus",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "PageLayoutCustomizedPageStatus",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "PageLayoutUrl",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "PageCustomizedPageStatus",
                table: "ClassicPageDiscoveries");
        }
    }
}
