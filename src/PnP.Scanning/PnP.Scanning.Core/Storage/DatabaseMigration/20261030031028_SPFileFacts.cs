using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class SPFileFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SPFile",
                columns: table => new
                {
                    SiteCollectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileUniqueId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FolderUniqueId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ListItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    Length = table.Column<long>(type: "INTEGER", nullable: true),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ServerRelativeUrl = table.Column<string>(type: "TEXT", nullable: true),
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
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClientSideApplicationId = table.Column<string>(type: "TEXT", nullable: true),
                    HtmlFileType = table.Column<string>(type: "TEXT", nullable: true),
                    FileType = table.Column<string>(type: "TEXT", nullable: true),
                    BSN = table.Column<string>(type: "TEXT", nullable: true),
                    WikiFieldHasValue = table.Column<bool>(type: "INTEGER", nullable: true),
                    ItemMetadataReadState = table.Column<string>(type: "TEXT", nullable: false),
                    ItemMetadataFieldStatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    NoExecuteRaw = table.Column<string>(type: "TEXT", nullable: true),
                    NoExecuteSource = table.Column<string>(type: "TEXT", nullable: true),
                    NoExecuteReadState = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SPFile", x => new { x.SiteCollectionId, x.FileUniqueId });
                    table.CheckConstraint("CK_SPFile_RealFile", "\"SiteCollectionId\" <> '00000000-0000-0000-0000-000000000000' AND \"FileUniqueId\" <> '00000000-0000-0000-0000-000000000000'");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SPFile");
        }
    }
}
