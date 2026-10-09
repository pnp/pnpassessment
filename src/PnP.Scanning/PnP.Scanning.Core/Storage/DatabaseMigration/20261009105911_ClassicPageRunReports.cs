using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    /// <inheritdoc />
    public partial class ClassicPageRunReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassicPageReportRows",
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
                    table.PrimaryKey("PK_ClassicPageReportRows", x => new { x.AnalysisRunId, x.Kind, x.RowKey });
                    table.ForeignKey(
                        name: "FK_ClassicPageReportRows_AnalysisRuns_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "AnalysisRuns",
                        principalColumn: "AnalysisRunId",
                        onDelete: ReferentialAction.Restrict);
                });
            foreach (var operation in new[] { "UPDATE", "DELETE" })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER CP_ReportRows_immutable_{operation} BEFORE {operation} ON ClassicPageReportRows
                    BEGIN SELECT RAISE(ABORT, 'published report rows are immutable'); END;
                    """);
            migrationBuilder.Sql("""
                CREATE TRIGGER CP_ReportRows_running_analysis BEFORE INSERT ON ClassicPageReportRows
                WHEN NOT EXISTS (SELECT 1 FROM PhaseRuns WHERE RunId = NEW.AnalysisRunId AND Kind = 'Analysis' AND Status = 2)
                BEGIN SELECT RAISE(ABORT, 'report publication requires a running analysis'); END;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassicPageReportRows");
        }
    }
}
