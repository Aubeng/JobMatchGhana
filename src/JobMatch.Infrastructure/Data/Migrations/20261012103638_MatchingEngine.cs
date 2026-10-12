using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobMatch.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatchingEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "Matches",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveredUtc",
                table: "Matches",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedbackText",
                table: "Matches",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InterestedUtc",
                table: "Matches",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Rank",
                table: "Matches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RatedUtc",
                table: "Matches",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Matches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Availability",
                table: "JobPostings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "District",
                table: "JobPostings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmployerName",
                table: "JobPostings",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresUtc",
                table: "JobPostings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSynthetic",
                table: "JobPostings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PayMax",
                table: "JobPostings",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PayMin",
                table: "JobPostings",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayPeriod",
                table: "JobPostings",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "JobPostings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradeCategoryId",
                table: "JobPostings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_Status_District",
                table: "JobPostings",
                columns: new[] { "Status", "District" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobPostings_Status_District",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "DeliveredUtc",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "FeedbackText",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "InterestedUtc",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "Rank",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "RatedUtc",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "Availability",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "District",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "EmployerName",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "ExpiresUtc",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "IsSynthetic",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "PayMax",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "PayMin",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "PayPeriod",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "TradeCategoryId",
                table: "JobPostings");
        }
    }
}
