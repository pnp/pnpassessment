using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicPublishingLayoutReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LayoutReferenceReason",
                table: "ClassicPages",
                type: "TEXT",
                nullable: false,
                defaultValue: "NotEvaluated");

            migrationBuilder.AddColumn<string>(
                name: "LayoutReferenceStatus",
                table: "ClassicPages",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "LayoutUrl",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LayoutReferenceReason",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "LayoutReferenceStatus",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "LayoutUrl",
                table: "ClassicPages");
        }
    }
}
