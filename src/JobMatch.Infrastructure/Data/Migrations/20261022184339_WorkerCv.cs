using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobMatch.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkerCv : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerCvs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerId = table.Column<int>(type: "int", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Training = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Education = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Languages = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    About = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ConsentUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSentUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TimesSent = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerCvs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerCvs_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkerCvJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerCvId = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SourceUtterance = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerCvJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerCvJobs_WorkerCvs_WorkerCvId",
                        column: x => x.WorkerCvId,
                        principalTable: "WorkerCvs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerCvJobs_WorkerCvId",
                table: "WorkerCvJobs",
                column: "WorkerCvId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerCvs_WorkerId",
                table: "WorkerCvs",
                column: "WorkerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerCvJobs");

            migrationBuilder.DropTable(
                name: "WorkerCvs");
        }
    }
}
