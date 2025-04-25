using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class v1110 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    AlertId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AlertTitle = table.Column<string>(type: "TEXT", nullable: true),
                    AlertType = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    DeliveryChannel = table.Column<string>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", nullable: true),
                    AlertFrequency = table.Column<string>(type: "TEXT", nullable: true),
                    CAMLQuery = table.Column<string>(type: "TEXT", nullable: true),
                    Filter = table.Column<string>(type: "TEXT", nullable: true),
                    UserLoginName = table.Column<string>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", nullable: true),
                    UserPrincipalType = table.Column<string>(type: "TEXT", nullable: true),
                    UserEmail = table.Column<string>(type: "TEXT", nullable: true),
                    ListUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListTitle = table.Column<string>(type: "TEXT", nullable: true),
                    AlertTemplateName = table.Column<string>(type: "TEXT", nullable: true),
                    AlertTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ListItemId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.AlertId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ScanId_SiteUrl_WebUrl_AlertId",
                table: "Alerts",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "AlertId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");
        }
    }
}
