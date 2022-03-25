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
                    PostScanStatus = table.Column<int>(type: "INTEGER", nullable: false),
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
                    ScanDuration = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StackTrace = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteCollections", x => new { x.ScanId, x.SiteUrl });
                });

            migrationBuilder.CreateTable(
                name: "SyntexContentTypeFields",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentTypeId = table.Column<string>(type: "TEXT", nullable: false),
                    FieldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InternalName = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    TypeAsString = table.Column<string>(type: "TEXT", nullable: true),
                    Required = table.Column<bool>(type: "INTEGER", nullable: false),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    TermSetId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntexContentTypeFields", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.ListId, x.ContentTypeId, x.FieldId });
                });

            migrationBuilder.CreateTable(
                name: "SyntexContentTypeOverview",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentTypeId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Group = table.Column<string>(type: "TEXT", nullable: true),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    FieldCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ListCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCountMean = table.Column<double>(type: "REAL", nullable: false),
                    ItemCountStandardDeviation = table.Column<double>(type: "REAL", nullable: false),
                    ItemCountMin = table.Column<double>(type: "REAL", nullable: false),
                    ItemCountMax = table.Column<double>(type: "REAL", nullable: false),
                    ItemCountMedian = table.Column<double>(type: "REAL", nullable: false),
                    ItemCountLowerQuartile = table.Column<double>(type: "REAL", nullable: false),
                    ItemCountUpperQuartile = table.Column<double>(type: "REAL", nullable: false),
                    IsSyntexContentType = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyntexModelDriveId = table.Column<string>(type: "TEXT", nullable: true),
                    SyntexModelObjectId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntexContentTypeOverview", x => new { x.ScanId, x.ContentTypeId });
                });

            migrationBuilder.CreateTable(
                name: "SyntexContentTypes",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentTypeId = table.Column<string>(type: "TEXT", nullable: false),
                    ListContentTypeId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Group = table.Column<string>(type: "TEXT", nullable: true),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    FieldCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntexContentTypes", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.ListId, x.ContentTypeId });
                });

            migrationBuilder.CreateTable(
                name: "SyntexFields",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FieldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InternalName = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    TypeAsString = table.Column<string>(type: "TEXT", nullable: true),
                    Required = table.Column<bool>(type: "INTEGER", nullable: false),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    TermSetId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntexFields", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.ListId, x.FieldId });
                });

            migrationBuilder.CreateTable(
                name: "SyntexLists",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListServerRelativeUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    ListTemplate = table.Column<int>(type: "INTEGER", nullable: false),
                    ListTemplateString = table.Column<string>(type: "TEXT", nullable: true),
                    AllowContentTypes = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContentTypeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ListExperienceOptions = table.Column<string>(type: "TEXT", nullable: true),
                    WorkflowInstanceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FlowInstanceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RetentionLabelCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FolderCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageDocumentsPerFolder = table.Column<int>(type: "INTEGER", nullable: false),
                    LibrarySize = table.Column<string>(type: "TEXT", nullable: true),
                    UsesCustomColumns = table.Column<bool>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastChanged = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastChangedYear = table.Column<int>(type: "INTEGER", nullable: false),
                    LastChangedMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    LastChangedMonthString = table.Column<string>(type: "TEXT", nullable: true),
                    LastChangedQuarter = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntexLists", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.ListId });
                });

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

            migrationBuilder.CreateTable(
                name: "TestDelays",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Delay1 = table.Column<int>(type: "INTEGER", nullable: false),
                    Delay2 = table.Column<int>(type: "INTEGER", nullable: false),
                    Delay3 = table.Column<int>(type: "INTEGER", nullable: false),
                    WebIdString = table.Column<string>(type: "TEXT", nullable: true)
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
                    WebUrlAbsolute = table.Column<string>(type: "TEXT", nullable: true),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScanDuration = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Template = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StackTrace = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webs", x => new { x.ScanId, x.SiteUrl, x.WebUrl });
                });

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
                    ContentTypeId = table.Column<string>(type: "TEXT", nullable: true),
                    ContentTypeName = table.Column<string>(type: "TEXT", nullable: true),
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
                    LastDefinitionEdit = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TotalInstances = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedInstances = table.Column<int>(type: "INTEGER", nullable: false),
                    NotStartedInstances = table.Column<int>(type: "INTEGER", nullable: false),
                    CancelledInstances = table.Column<int>(type: "INTEGER", nullable: false),
                    CancellingInstances = table.Column<int>(type: "INTEGER", nullable: false),
                    SuspendedInstances = table.Column<int>(type: "INTEGER", nullable: false),
                    TerminatedInstances = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedInstances = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => new { x.ScanId, x.SiteUrl, x.WebUrl, x.DefinitionId, x.SubscriptionId });
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
                name: "IX_SyntexContentTypeFields_ScanId_SiteUrl_WebUrl_ListId_ContentTypeId_FieldId",
                table: "SyntexContentTypeFields",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "ListId", "ContentTypeId", "FieldId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyntexContentTypeOverview_ScanId_ContentTypeId",
                table: "SyntexContentTypeOverview",
                columns: new[] { "ScanId", "ContentTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyntexContentTypes_ScanId_ContentTypeId",
                table: "SyntexContentTypes",
                columns: new[] { "ScanId", "ContentTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyntexContentTypes_ScanId_SiteUrl_WebUrl_ListId_ContentTypeId",
                table: "SyntexContentTypes",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "ListId", "ContentTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyntexFields_ScanId_SiteUrl_WebUrl_ListId_FieldId",
                table: "SyntexFields",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "ListId", "FieldId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyntexLists_ScanId_SiteUrl_WebUrl_ListId",
                table: "SyntexLists",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "ListId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyntexModelUsage_ScanId_SiteUrl_WebUrl_Classifier_TargetSiteId_TargetWebId_TargetListId",
                table: "SyntexModelUsage",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "Classifier", "TargetSiteId", "TargetWebId", "TargetListId" });

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

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_ScanId_SiteUrl_WebUrl_DefinitionId_SubscriptionId",
                table: "Workflows",
                columns: new[] { "ScanId", "SiteUrl", "WebUrl", "DefinitionId", "SubscriptionId" },
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
                name: "SyntexContentTypeFields");

            migrationBuilder.DropTable(
                name: "SyntexContentTypeOverview");

            migrationBuilder.DropTable(
                name: "SyntexContentTypes");

            migrationBuilder.DropTable(
                name: "SyntexFields");

            migrationBuilder.DropTable(
                name: "SyntexLists");

            migrationBuilder.DropTable(
                name: "SyntexModelUsage");

            migrationBuilder.DropTable(
                name: "TestDelays");

            migrationBuilder.DropTable(
                name: "Webs");

            migrationBuilder.DropTable(
                name: "Workflows");
        }
    }
}
