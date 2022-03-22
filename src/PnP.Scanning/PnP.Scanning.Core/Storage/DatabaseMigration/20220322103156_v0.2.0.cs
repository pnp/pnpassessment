using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class v020 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyntexModelUsage",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Classifier = table.Column<string>(type: "TEXT", nullable: false),
                    TargetSiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetWebId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassifiedItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NotProcessedItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageConfidenceScore = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntexModelUsage", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.Classifier, x.TargetSiteId, x.TargetWebId, x.TargetListId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyntexModelUsage_ScanId_SiteUrl_WebUrl_Classifier_TargetSiteId_TargetWebId_TargetListId",
                table: "SyntexModelUsage",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "Classifier", "TargetSiteId", "TargetWebId", "TargetListId" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyntexModelUsage");
        }
    }
}
