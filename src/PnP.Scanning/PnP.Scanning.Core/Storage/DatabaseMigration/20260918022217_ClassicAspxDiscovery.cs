using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicAspxDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "HomePage",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "AssessmentStatus",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryStatus",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FileUniqueId",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ListItemId",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SiteCollectionId",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WebId",
                table: "ClassicPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClassicPageDiscoveries",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordKey = table.Column<string>(type: "TEXT", nullable: false),
                    RowType = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeType = table.Column<string>(type: "TEXT", nullable: true),
                    ParentScopeKey = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    SiteCollectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WebId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FolderUniqueId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FileUniqueId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ListItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    PageType = table.Column<string>(type: "TEXT", nullable: true),
                    ContentTypeId = table.Column<string>(type: "TEXT", nullable: true),
                    HomePage = table.Column<bool>(type: "INTEGER", nullable: true),
                    LibraryHidden = table.Column<bool>(type: "INTEGER", nullable: true),
                    ObservationMethod = table.Column<string>(type: "TEXT", nullable: true),
                    DiscoveryStatus = table.Column<string>(type: "TEXT", nullable: false),
                    AssessmentStatus = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedChildCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ObservedChildCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorStage = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorCodes = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicPageDiscoveries", x => new { x.ScanId, x.RecordKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicPageDiscoveries_ScanId_SiteUrl_WebUrl",
                table: "ClassicPageDiscoveries",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicPageDiscoveries");

            migrationBuilder.DropColumn(
                name: "AssessmentStatus",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "DiscoveryStatus",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "FileUniqueId",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "ListItemId",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "SiteCollectionId",
                table: "ClassicPages");

            migrationBuilder.DropColumn(
                name: "WebId",
                table: "ClassicPages");

            migrationBuilder.AlterColumn<bool>(
                name: "HomePage",
                table: "ClassicPages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldNullable: true);

        }
    }
}
