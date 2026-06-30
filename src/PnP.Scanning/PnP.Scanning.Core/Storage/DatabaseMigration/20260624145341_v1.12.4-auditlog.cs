using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1124auditlog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassicPageAuditUsages",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", nullable: false),
                    AuditViewsCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AuditUniqueUsers = table.Column<int>(type: "INTEGER", nullable: false),
                    AuditWindowStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AuditWindowEnd = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QueryStatus = table.Column<string>(type: "TEXT", nullable: true),
                    SkipReason = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicPageAuditUsages", x => new { x.ScanId, x.SiteUrl, x.PageUrl });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicPageAuditUsages_ScanId_SiteUrl_PageUrl",
                table: "ClassicPageAuditUsages",
                columns: new[] { "ScanId", "SiteUrl", "PageUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicPageAuditUsages");
        }
    }
}
