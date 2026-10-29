using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicAspxInventoryOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassicAspxInventories",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AspxKey = table.Column<string>(type: "TEXT", nullable: false),
                    SiteCollectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FolderUniqueId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FileUniqueId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    ContentTypeId = table.Column<string>(type: "TEXT", nullable: true),
                    CustomizedPageStatus = table.Column<string>(type: "TEXT", nullable: true),
                    SetupPath = table.Column<string>(type: "TEXT", nullable: true),
                    FileReadState = table.Column<string>(type: "TEXT", nullable: false),
                    FileErrorCodes = table.Column<string>(type: "TEXT", nullable: true),
                    FileErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    ProvenanceJson = table.Column<string>(type: "TEXT", nullable: false),
                    CoverageState = table.Column<string>(type: "TEXT", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassicAspxInventories", x => new { x.ScanId, x.AspxKey });
                    table.CheckConstraint("CK_ClassicAspxInventories_RealFile", "length(\"AspxKey\") > 0 AND \"SiteCollectionId\" <> '00000000-0000-0000-0000-000000000000' AND \"FileUniqueId\" <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassicAspxInventories_ScanId_SiteCollectionId_FileUniqueId",
                table: "ClassicAspxInventories",
                columns: new[] { "ScanId", "SiteCollectionId", "FileUniqueId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicAspxInventories");
        }
    }
}
