using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobMatch.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobPostings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LocationText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmployerContact = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Embedding = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPostings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WaMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    WaId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MessageType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IntentDetected = table.Column<int>(type: "int", nullable: true),
                    SessionState = table.Column<int>(type: "int", nullable: true),
                    NluConfidence = table.Column<double>(type: "float", nullable: true),
                    IsRepair = table.Column<bool>(type: "bit", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CanonicalName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Embedding = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WaId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    PreferredLanguage = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    LocationText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConsentGiven = table.Column<bool>(type: "bit", nullable: false),
                    ConsentUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobPostingSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobPostingId = table.Column<int>(type: "int", nullable: false),
                    SkillId = table.Column<int>(type: "int", nullable: false),
                    Weight = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPostingSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobPostingSkills_JobPostings_JobPostingId",
                        column: x => x.JobPostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobPostingSkills_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Matches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerId = table.Column<int>(type: "int", nullable: false),
                    JobPostingId = table.Column<int>(type: "int", nullable: false),
                    SemanticScore = table.Column<double>(type: "float", nullable: false),
                    FinalScore = table.Column<double>(type: "float", nullable: false),
                    ScoreBreakdownJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Matches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Matches_JobPostings_JobPostingId",
                        column: x => x.JobPostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Matches_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkerSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerId = table.Column<int>(type: "int", nullable: false),
                    SkillId = table.Column<int>(type: "int", nullable: false),
                    SourceUtterance = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExtractionConfidence = table.Column<double>(type: "float", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerSkills_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkerSkills_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostingSkills_JobPostingId",
                table: "JobPostingSkills",
                column: "JobPostingId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostingSkills_SkillId",
                table: "JobPostingSkills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_Matches_JobPostingId",
                table: "Matches",
                column: "JobPostingId");

            migrationBuilder.CreateIndex(
                name: "IX_Matches_WorkerId_JobPostingId",
                table: "Matches",
                columns: new[] { "WorkerId", "JobPostingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageLogs_Timestamp",
                table: "MessageLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_MessageLogs_WaId",
                table: "MessageLogs",
                column: "WaId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageLogs_WaMessageId",
                table: "MessageLogs",
                column: "WaMessageId",
                unique: true,
                filter: "[WaMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_CanonicalName",
                table: "Skills",
                column: "CanonicalName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workers_WaId",
                table: "Workers",
                column: "WaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerSkills_SkillId",
                table: "WorkerSkills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerSkills_WorkerId_SkillId",
                table: "WorkerSkills",
                columns: new[] { "WorkerId", "SkillId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobPostingSkills");

            migrationBuilder.DropTable(
                name: "Matches");

            migrationBuilder.DropTable(
                name: "MessageLogs");

            migrationBuilder.DropTable(
                name: "WorkerSkills");

            migrationBuilder.DropTable(
                name: "JobPostings");

            migrationBuilder.DropTable(
                name: "Skills");

            migrationBuilder.DropTable(
                name: "Workers");
        }
    }
}
