using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class syntex1 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetentionLabelCount",
                table: "SyntexLists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetentionLabelCount",
                table: "SyntexLists");
        }
    }
}
