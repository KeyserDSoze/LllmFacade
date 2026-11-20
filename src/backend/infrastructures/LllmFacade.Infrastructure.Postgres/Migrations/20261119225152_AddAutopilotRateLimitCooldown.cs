using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LllmFacade.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAutopilotRateLimitCooldown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RateLimitUntil",
                table: "tracked_projects",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RateLimitUntil",
                table: "tracked_projects");
        }
    }
}
