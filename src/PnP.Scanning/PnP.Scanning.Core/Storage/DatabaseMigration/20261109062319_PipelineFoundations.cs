using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class PipelineFoundations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AspxEvidenceJson",
                table: "SPFile",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SourceSnapshots",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModuleKey = table.Column<string>(type: "TEXT", nullable: false),
                    InputVersion = table.Column<string>(type: "TEXT", nullable: false),
                    FormatVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ScopeJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsSealed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SealedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MemberIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: true),
                    ManifestDigest = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSnapshots", x => x.SnapshotId);
                    table.UniqueConstraint("AK_SourceSnapshots_SnapshotId_AssessmentId", x => new { x.SnapshotId, x.AssessmentId });
                    table.ForeignKey(
                        name: "FK_SourceSnapshots_Scans_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "Scans",
                        principalColumn: "ScanId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhaseRuns",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentPhase = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ModuleKey = table.Column<string>(type: "TEXT", nullable: false),
                    InputVersion = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleVersion = table.Column<string>(type: "TEXT", nullable: true),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedRecords = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalRecords = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CheckpointJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhaseRuns", x => x.RunId);
                    table.UniqueConstraint("AK_PhaseRuns_RunId_AssessmentId", x => new { x.RunId, x.AssessmentId });
                    table.UniqueConstraint("AK_PhaseRuns_RunId_SnapshotId", x => new { x.RunId, x.SnapshotId });
                    table.ForeignKey(
                        name: "FK_PhaseRuns_PhaseRuns_ParentRunId_AssessmentId",
                        columns: x => new { x.ParentRunId, x.AssessmentId },
                        principalTable: "PhaseRuns",
                        principalColumns: new[] { "RunId", "AssessmentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhaseRuns_SourceSnapshots_SnapshotId_AssessmentId",
                        columns: x => new { x.SnapshotId, x.AssessmentId },
                        principalTable: "SourceSnapshots",
                        principalColumns: new[] { "SnapshotId", "AssessmentId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceObservations",
                columns: table => new
                {
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRevision = table.Column<string>(type: "TEXT", nullable: true),
                    AcquisitionStatus = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    AcquisitionError = table.Column<string>(type: "TEXT", nullable: true),
                    RawBytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Length = table.Column<long>(type: "INTEGER", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceObservations", x => x.ObservationId);
                    table.UniqueConstraint("AK_SourceObservations_ObservationId_SnapshotId", x => new { x.ObservationId, x.SnapshotId });
                    table.ForeignKey(
                        name: "FK_SourceObservations_SourceSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "SourceSnapshots",
                        principalColumn: "SnapshotId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisRuns",
                columns: table => new
                {
                    AnalysisRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModuleKey = table.Column<string>(type: "TEXT", nullable: false),
                    RuleVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestDigest = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReportDigest = table.Column<string>(type: "TEXT", nullable: true),
                    ReportPublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisRuns", x => x.AnalysisRunId);
                    table.UniqueConstraint("AK_AnalysisRuns_AnalysisRunId_SnapshotId", x => new { x.AnalysisRunId, x.SnapshotId });
                    table.ForeignKey(
                        name: "FK_AnalysisRuns_PhaseRuns_AnalysisRunId_SnapshotId",
                        columns: x => new { x.AnalysisRunId, x.SnapshotId },
                        principalTable: "PhaseRuns",
                        principalColumns: new[] { "RunId", "SnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisReportRows",
                columns: table => new
                {
                    AnalysisRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RowKey = table.Column<string>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisReportRows", x => new { x.AnalysisRunId, x.Kind, x.RowKey });
                    table.ForeignKey(
                        name: "FK_AnalysisReportRows_AnalysisRuns_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "AnalysisRuns",
                        principalColumn: "AnalysisRunId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisResults",
                columns: table => new
                {
                    AnalysisRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CommittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisResults", x => new { x.AnalysisRunId, x.ObservationId });
                    table.ForeignKey(
                        name: "FK_AnalysisResults_AnalysisRuns_AnalysisRunId_SnapshotId",
                        columns: x => new { x.AnalysisRunId, x.SnapshotId },
                        principalTable: "AnalysisRuns",
                        principalColumns: new[] { "AnalysisRunId", "SnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisResults_SourceObservations_ObservationId_SnapshotId",
                        columns: x => new { x.ObservationId, x.SnapshotId },
                        principalTable: "SourceObservations",
                        principalColumns: new[] { "ObservationId", "SnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisResults_AnalysisRunId_SnapshotId",
                table: "AnalysisResults",
                columns: new[] { "AnalysisRunId", "SnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisResults_ObservationId_SnapshotId",
                table: "AnalysisResults",
                columns: new[] { "ObservationId", "SnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_PhaseRuns_AssessmentId_ParentRunId_CreatedAtUtc",
                table: "PhaseRuns",
                columns: new[] { "AssessmentId", "ParentRunId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PhaseRuns_ParentRunId_AssessmentId",
                table: "PhaseRuns",
                columns: new[] { "ParentRunId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_PhaseRuns_SnapshotId_AssessmentId",
                table: "PhaseRuns",
                columns: new[] { "SnapshotId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceObservations_SnapshotId_SourceIdentity",
                table: "SourceObservations",
                columns: new[] { "SnapshotId", "SourceIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshots_AssessmentId",
                table: "SourceSnapshots",
                column: "AssessmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisReportRows");

            migrationBuilder.DropTable(
                name: "AnalysisResults");

            migrationBuilder.DropTable(
                name: "AnalysisRuns");

            migrationBuilder.DropTable(
                name: "SourceObservations");

            migrationBuilder.DropTable(
                name: "PhaseRuns");

            migrationBuilder.DropTable(
                name: "SourceSnapshots");

            migrationBuilder.DropColumn(
                name: "AspxEvidenceJson",
                table: "SPFile");
        }
    }
}
