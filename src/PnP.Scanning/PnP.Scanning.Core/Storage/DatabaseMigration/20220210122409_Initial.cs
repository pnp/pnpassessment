using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class Initial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cache",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cache", x => new { x.ScanId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "History",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Event = table.Column<string>(type: "TEXT", nullable: true),
                    EventDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Properties",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Properties", x => new { x.ScanId, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "Scans",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PreScanStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    CLIMode = table.Column<string>(type: "TEXT", nullable: true),
                    CLITenant = table.Column<string>(type: "TEXT", nullable: true),
                    CLITenantId = table.Column<string>(type: "TEXT", nullable: true),
                    CLIEnvironment = table.Column<string>(type: "TEXT", nullable: true),
                    CLISiteList = table.Column<string>(type: "TEXT", nullable: true),
                    CLISiteFile = table.Column<string>(type: "TEXT", nullable: true),
                    CLIAuthMode = table.Column<string>(type: "TEXT", nullable: true),
                    CLIApplicationId = table.Column<string>(type: "TEXT", nullable: true),
                    CLICertPath = table.Column<string>(type: "TEXT", nullable: true),
                    CLICertFile = table.Column<string>(type: "TEXT", nullable: true),
                    CLICertFilePassword = table.Column<string>(type: "TEXT", nullable: true),
                    CLIThreads = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scans", x => x.ScanId);
                });

            migrationBuilder.CreateTable(
                name: "SiteCollections",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StackTrace = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteCollections", x => new { x.ScanId, x.SiteUrl });
                });

            migrationBuilder.CreateTable(
                name: "TestDelays",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Delay1 = table.Column<int>(type: "INTEGER", nullable: false),
                    Delay2 = table.Column<int>(type: "INTEGER", nullable: false),
                    Delay3 = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestDelays", x => new { x.ScanId, x.SiteUrl, x.WebUrl });
                });

            migrationBuilder.CreateTable(
                name: "Webs",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Template = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StackTrace = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webs", x => new { x.ScanId, x.SiteUrl, x.WebUrl });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cache_ScanId_Key",
                table: "Cache",
                columns: new[] { "ScanId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_History_ScanId_Event_EventDate",
                table: "History",
                columns: new[] { "ScanId", "Event", "EventDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Properties_ScanId_Name",
                table: "Properties",
                columns: new[] { "ScanId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SiteCollections_ScanId_SiteUrl",
                table: "SiteCollections",
                columns: new[] { "ScanId", "SiteUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestDelays_ScanId_SiteUrl_WebUrl",
                table: "TestDelays",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Webs_ScanId_SiteUrl_WebUrl",
                table: "Webs",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cache");

            migrationBuilder.DropTable(
                name: "History");

            migrationBuilder.DropTable(
                name: "Properties");

            migrationBuilder.DropTable(
                name: "Scans");

            migrationBuilder.DropTable(
                name: "SiteCollections");

            migrationBuilder.DropTable(
                name: "TestDelays");

            migrationBuilder.DropTable(
                name: "Webs");
        }
    }
}
