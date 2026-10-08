using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicPageHandler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PageHandler",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageHandlerEvidenceJson",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PageHandler",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "PageHandlerEvidenceJson",
                table: "ClassicPages");
        }
    }
}
