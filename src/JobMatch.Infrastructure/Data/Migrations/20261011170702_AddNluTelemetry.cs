using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobMatch.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNluTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NluEntitiesJson",
                table: "MessageLogs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NluIntent",
                table: "MessageLogs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "NluIntentConfidence",
                table: "MessageLogs",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "NluLatencyMs",
                table: "MessageLogs",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NluModelVersion",
                table: "MessageLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NluEntitiesJson",
                table: "MessageLogs");

            migrationBuilder.DropColumn(
                name: "NluIntent",
                table: "MessageLogs");

            migrationBuilder.DropColumn(
                name: "NluIntentConfidence",
                table: "MessageLogs");

            migrationBuilder.DropColumn(
                name: "NluLatencyMs",
                table: "MessageLogs");

            migrationBuilder.DropColumn(
                name: "NluModelVersion",
                table: "MessageLogs");
        }
    }
}
