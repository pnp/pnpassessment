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
                name: "CustomizedPageStatus",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LayoutCustomizedPageStatus",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LayoutUrl",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomizedPageStatus",
                table: "ClassicPageDiscoveries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomizedPageStatus",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "LayoutCustomizedPageStatus",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "LayoutUrl",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "CustomizedPageStatus",
                table: "ClassicPageDiscoveries");
        }
    }
}
