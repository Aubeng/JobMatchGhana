using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobMatch.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlignDialogueWithSdHsm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemovedUtc",
                table: "WorkerSkills",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Availability",
                table: "Workers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExperienceText",
                table: "Workers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ExperienceYears",
                table: "Workers",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationDistrict",
                table: "Workers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationRegion",
                table: "Workers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProfileConfirmedUtc",
                table: "Workers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradeCategoryId",
                table: "Workers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradeName",
                table: "Workers",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workers_LocationDistrict",
                table: "Workers",
                column: "LocationDistrict");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workers_LocationDistrict",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "RemovedUtc",
                table: "WorkerSkills");

            migrationBuilder.DropColumn(
                name: "Availability",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ExperienceText",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ExperienceYears",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LocationDistrict",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LocationRegion",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ProfileConfirmedUtc",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "TradeCategoryId",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "TradeName",
                table: "Workers");
        }
    }
}
