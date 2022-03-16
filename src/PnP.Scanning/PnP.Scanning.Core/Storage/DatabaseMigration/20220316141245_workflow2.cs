using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PnP.Scanning.Core.Storage.DatabaseMigration
{
    public partial class workflow2 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CancelledInstances",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CancellingInstances",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CompletedInstances",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NotStartedInstances",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StartedInstances",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SuspendedInstances",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TerminatedInstances",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalInstances",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelledInstances",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "CancellingInstances",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "CompletedInstances",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "NotStartedInstances",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "StartedInstances",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "SuspendedInstances",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "TerminatedInstances",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "TotalInstances",
                table: "Workflows");
        }
    }
}
