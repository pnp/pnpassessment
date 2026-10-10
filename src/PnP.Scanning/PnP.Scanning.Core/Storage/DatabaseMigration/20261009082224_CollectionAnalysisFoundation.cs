using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class CollectionAnalysisFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    AnalysisRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RuleVersion = table.Column<string>(type: "TEXT", nullable: true),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnalysisParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    CollectionOptionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Threads = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedRecords = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalRecords = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CheckpointJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhaseRuns", x => x.RunId);
                    table.UniqueConstraint("AK_PhaseRuns_RunId_SnapshotId", x => new { x.RunId, x.SnapshotId });
                    table.ForeignKey(
                        name: "FK_PhaseRuns_PhaseRuns_ParentRunId",
                        column: x => x.ParentRunId,
                        principalTable: "PhaseRuns",
                        principalColumn: "RunId",
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
                    ArtifactId = table.Column<Guid>(type: "TEXT", nullable: false)
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
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                name: "SourceArtifacts",
                columns: table => new
                {
                    ArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RawBytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Length = table.Column<long>(type: "INTEGER", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceArtifacts", x => x.ArtifactId);
                    table.ForeignKey(
                        name: "FK_SourceArtifacts_SourceObservations_ObservationId_SnapshotId",
                        columns: x => new { x.ObservationId, x.SnapshotId },
                        principalTable: "SourceObservations",
                        principalColumns: new[] { "ObservationId", "SnapshotId" },
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
                name: "IX_PhaseRuns_ParentRunId",
                table: "PhaseRuns",
                column: "ParentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PhaseRuns_SnapshotId_AssessmentId",
                table: "PhaseRuns",
                columns: new[] { "SnapshotId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceArtifacts_ObservationId_SnapshotId",
                table: "SourceArtifacts",
                columns: new[] { "ObservationId", "SnapshotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceObservations_SnapshotId_SourceIdentity",
                table: "SourceObservations",
                columns: new[] { "SnapshotId", "SourceIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshots_AssessmentId",
                table: "SourceSnapshots",
                column: "AssessmentId");

            // Defense at the storage boundary, in addition to the separate module capabilities.
            foreach (var table in new[] { "SourceObservations", "SourceArtifacts" })
            {
                foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                {
                    var reference = operation == "DELETE" ? "OLD" : "NEW";
                    var condition = $"EXISTS (SELECT 1 FROM SourceSnapshots WHERE SnapshotId = {reference}.SnapshotId AND IsSealed = 1)";
                    if (operation == "UPDATE")
                        condition += " OR EXISTS (SELECT 1 FROM SourceSnapshots WHERE SnapshotId = OLD.SnapshotId AND IsSealed = 1)";
                    migrationBuilder.Sql($"""
                        CREATE TRIGGER Pipeline_{table}_sealed_{operation} BEFORE {operation} ON {table}
                        WHEN {condition}
                        BEGIN SELECT RAISE(ABORT, 'sealed source data is immutable'); END;
                        """);
                }
            }
            foreach (var operation in new[] { "UPDATE", "DELETE" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER Pipeline_SourceSnapshots_sealed_{operation} BEFORE {operation} ON SourceSnapshots
                    WHEN OLD.IsSealed = 1
                    BEGIN SELECT RAISE(ABORT, 'sealed snapshot is immutable'); END;
                    """);
                foreach (var table in new[] { "AnalysisRuns", "AnalysisResults" })
                    migrationBuilder.Sql($"""
                        CREATE TRIGGER Pipeline_{table}_immutable_{operation} BEFORE {operation} ON {table}
                        BEGIN SELECT RAISE(ABORT, 'analysis input and committed results are immutable'); END;
                        """);
            }
            foreach (var table in new[] { "AnalysisRuns", "AnalysisResults" })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER Pipeline_{table}_requires_sealed_input BEFORE INSERT ON {table}
                    WHEN NOT EXISTS (SELECT 1 FROM SourceSnapshots WHERE SnapshotId = NEW.SnapshotId AND IsSealed = 1)
                    BEGIN SELECT RAISE(ABORT, 'analysis requires a sealed snapshot'); END;
                    """);
            migrationBuilder.Sql("""
                CREATE TRIGGER Pipeline_PhaseRuns_fixed_input BEFORE UPDATE ON PhaseRuns
                WHEN OLD.AssessmentId IS NOT NEW.AssessmentId OR OLD.ParentRunId IS NOT NEW.ParentRunId
                    OR OLD.Kind IS NOT NEW.Kind OR OLD.ModuleKey IS NOT NEW.ModuleKey
                    OR OLD.InputVersion IS NOT NEW.InputVersion OR OLD.SnapshotId IS NOT NEW.SnapshotId
                    OR OLD.AnalysisRunId IS NOT NEW.AnalysisRunId OR OLD.RuleVersion IS NOT NEW.RuleVersion
                    OR OLD.ParametersJson IS NOT NEW.ParametersJson OR OLD.AnalysisParametersJson IS NOT NEW.AnalysisParametersJson
                    OR OLD.CollectionOptionsJson IS NOT NEW.CollectionOptionsJson
                BEGIN SELECT RAISE(ABORT, 'phase input and parameters are fixed at enqueue'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisResults");

            migrationBuilder.DropTable(
                name: "SourceArtifacts");

            migrationBuilder.DropTable(
                name: "AnalysisRuns");

            migrationBuilder.DropTable(
                name: "SourceObservations");

            migrationBuilder.DropTable(
                name: "PhaseRuns");

            migrationBuilder.DropTable(
                name: "SourceSnapshots");
        }
    }
}
