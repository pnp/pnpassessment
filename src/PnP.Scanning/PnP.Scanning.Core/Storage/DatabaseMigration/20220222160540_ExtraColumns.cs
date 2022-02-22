using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class ExtraColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebUrlAbsolute",
                table: "Webs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FlowInstanceCount",
                table: "SyntexLists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkflowInstanceCount",
                table: "SyntexLists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebUrlAbsolute",
                table: "Webs");

            migrationBuilder.DropColumn(
                name: "FlowInstanceCount",
                table: "SyntexLists");

            migrationBuilder.DropColumn(
                name: "WorkflowInstanceCount",
                table: "SyntexLists");
        }
    }
}
