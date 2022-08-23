using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class v130 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyntexFileTypes",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileType = table.Column<string>(type: "TEXT", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntexFileTypes", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.ListId, x.FileType });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyntexFileTypes_ScanId_FileType",
                table: "SyntexFileTypes",
                columns: new[] { "ScanId", "FileType" });

            migrationBuilder.CreateIndex(
                name: "IX_SyntexFileTypes_ScanId_SiteUrl_WebUrl_ListId_FileType",
                table: "SyntexFileTypes",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "ListId", "FileType" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyntexFileTypes");
        }
    }
}
