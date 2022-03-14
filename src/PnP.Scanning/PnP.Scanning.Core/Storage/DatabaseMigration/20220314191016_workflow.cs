using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class workflow : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ListTitle = table.Column<string>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsOOBWorkflow = table.Column<bool>(type: "INTEGER", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    RestrictToType = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefinitionName = table.Column<string>(type: "TEXT", nullable: true),
                    DefinitionDescription = table.Column<string>(type: "TEXT", nullable: true),
                    SubscriptionName = table.Column<string>(type: "TEXT", nullable: true),
                    HasSubscriptions = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UsedActions = table.Column<string>(type: "TEXT", nullable: true),
                    UnsupportedActionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnsupportedActionsInFlow = table.Column<string>(type: "TEXT", nullable: true),
                    UsedTriggers = table.Column<string>(type: "TEXT", nullable: true),
                    LastSubscriptionEdit = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastDefinitionEdit = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.DefinitionId, x.SubscriptionId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_ScanId_SiteUrl_WebUrl_DefinitionId_SubscriptionId",
                table: "Workflows",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "DefinitionId", "SubscriptionId" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Workflows");
        }
    }
}
