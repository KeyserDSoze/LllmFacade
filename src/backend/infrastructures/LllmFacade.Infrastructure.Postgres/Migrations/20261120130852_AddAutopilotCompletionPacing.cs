using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LllmFacade.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAutopilotCompletionPacing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRateLimitAt",
                table: "tracked_projects",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRateLimitAt",
                table: "tracked_projects");
        }
    }
}
