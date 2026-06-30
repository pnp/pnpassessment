using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1124classicpageaudit : Migration
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

            migrationBuilder.CreateTable(
                name: "ClassicPageAuditUsages",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", nullable: false),
                    AuditViewsCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AuditCreatesCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AuditEditsCount = table.Column<int>(type: "INTEGER", nullable: false),
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicPageAuditUsages");

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
