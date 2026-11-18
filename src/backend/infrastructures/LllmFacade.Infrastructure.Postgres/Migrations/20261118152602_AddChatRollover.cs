using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LllmFacade.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddChatRollover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChatsCreated",
                table: "tracked_projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "NewChatPrompt",
                table: "tracked_projects",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChatsCreated",
                table: "tracked_projects");

            migrationBuilder.DropColumn(
                name: "NewChatPrompt",
                table: "tracked_projects");
        }
    }
}
