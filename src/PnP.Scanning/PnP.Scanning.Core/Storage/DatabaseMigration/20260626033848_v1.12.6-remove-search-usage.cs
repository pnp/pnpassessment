using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1126removesearchusage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
