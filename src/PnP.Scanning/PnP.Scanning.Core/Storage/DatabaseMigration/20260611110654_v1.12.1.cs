using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1121 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MappingPercentage",
                table: "ClassicPages",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "UnmappedWebParts",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WebPartCount",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MappingPercentage",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "UnmappedWebParts",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "WebPartCount",
                table: "ClassicPages");
        }
    }
}
