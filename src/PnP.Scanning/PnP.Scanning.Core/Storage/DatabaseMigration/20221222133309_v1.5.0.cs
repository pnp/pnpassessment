using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class v150 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InfoPath",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ListTitle = table.Column<string>(type: "TEXT", nullable: true),
                    InfoPathUsage = table.Column<string>(type: "TEXT", nullable: true),
                    InfoPathTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastItemUserModifiedDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InfoPath", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.ListId });
                });

            migrationBuilder.CreateTable(
                name: "Pages",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PageName = table.Column<string>(type: "TEXT", nullable: true),
                    PageType = table.Column<string>(type: "TEXT", nullable: true),
                    ListUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ListTitle = table.Column<string>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pages", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.PageUrl });
                });

            migrationBuilder.CreateIndex(
                name: "IX_InfoPath_ScanId_SiteUrl_WebUrl_ListId",
                table: "InfoPath",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "ListId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pages_ScanId_SiteUrl_WebUrl_PageUrl",
                table: "Pages",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "PageUrl" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InfoPath");

            migrationBuilder.DropTable(
                name: "Pages");
        }
    }
}
