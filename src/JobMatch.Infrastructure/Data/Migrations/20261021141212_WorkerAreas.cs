using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobMatch.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkerAreas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerAreas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerId = table.Column<int>(type: "int", nullable: false),
                    District = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Region = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LocationText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerAreas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerAreas_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAreas_District",
                table: "WorkerAreas",
                column: "District");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAreas_WorkerId_District",
                table: "WorkerAreas",
                columns: new[] { "WorkerId", "District" },
                unique: true);

            // DL-018: existing profiles keep their one area as their main area.
            migrationBuilder.Sql(
                "INSERT INTO WorkerAreas (WorkerId, District, Region, LocationText, SortOrder) " +
                "SELECT Id, LocationDistrict, LocationRegion, LocationText, 0 FROM Workers WHERE LocationDistrict IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerAreas");
        }
    }
}
