using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class v160 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnterpriseContentCenter",
                table: "SyntexModelUsage",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ViewCount",
                table: "SyntexLists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DocumentTemplate",
                table: "SyntexContentTypes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentTemplate",
                table: "SyntexContentTypeOverview",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyntexTermSets",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TermSetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntexTermSets", x => new { x.ScanId, x.TermSetId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyntexTermSets_ScanId_TermSetId",
                table: "SyntexTermSets",
                columns: new[] { "ScanId", "TermSetId" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyntexTermSets");

            migrationBuilder.DropColumn(
                name: "EnterpriseContentCenter",
                table: "SyntexModelUsage");

            migrationBuilder.DropColumn(
                name: "ViewCount",
                table: "SyntexLists");

            migrationBuilder.DropColumn(
                name: "DocumentTemplate",
                table: "SyntexContentTypes");

            migrationBuilder.DropColumn(
                name: "DocumentTemplate",
                table: "SyntexContentTypeOverview");
        }
    }
}
