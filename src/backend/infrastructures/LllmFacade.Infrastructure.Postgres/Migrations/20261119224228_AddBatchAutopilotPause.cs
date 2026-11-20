using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LllmFacade.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchAutopilotPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BatchPaused",
                table: "tracked_projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatchPaused",
                table: "tracked_projects");
        }
    }
}
