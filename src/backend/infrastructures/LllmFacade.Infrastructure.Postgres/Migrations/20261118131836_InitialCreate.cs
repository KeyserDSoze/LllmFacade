using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LllmFacade.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "boolean", nullable: false),
                    SecurityStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PasswordChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "chatgpt_endpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Models = table.Column<string>(type: "jsonb", nullable: false),
                    ThinkingEfforts = table.Column<string>(type: "jsonb", nullable: false),
                    UseTemporaryChat = table.Column<bool>(type: "boolean", nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExtraHeaders = table.Column<string>(type: "jsonb", nullable: false),
                    ApiKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApiKeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProtectedAccessToken = table.Column<string>(type: "text", nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProtectedStorageState = table.Column<string>(type: "text", nullable: false),
                    StorageStateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CookieCount = table.Column<int>(type: "integer", nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    CredentialsStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    LastRefreshAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRefreshAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chatgpt_endpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tracked_projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ContinuationPrompt = table.Column<string>(type: "text", nullable: false),
                    TargetConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AutopilotEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxContinuations = table.Column<int>(type: "integer", nullable: true),
                    ContinuationsSent = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CurrentConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastContinuationAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingAfterNodeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracked_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracked_projects_chatgpt_endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "chatgpt_endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_users_UserName",
                table: "admin_users",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chatgpt_endpoints_ApiKeyPrefix",
                table: "chatgpt_endpoints",
                column: "ApiKeyPrefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracked_projects_AutopilotEnabled_NextCheckAt",
                table: "tracked_projects",
                columns: new[] { "AutopilotEnabled", "NextCheckAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tracked_projects_EndpointId_ProjectId",
                table: "tracked_projects",
                columns: new[] { "EndpointId", "ProjectId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_users");

            migrationBuilder.DropTable(
                name: "tracked_projects");

            migrationBuilder.DropTable(
                name: "chatgpt_endpoints");
        }
    }
}
