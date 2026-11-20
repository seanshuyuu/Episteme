using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Episteme.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpAccessPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpUserSettings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AllowCreateWorkspace = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpUserSettings", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_McpUserSettings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpWorkspaceSettings",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpWorkspaceSettings", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_McpWorkspaceSettings_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpWorkspaceUserSettings",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonalPermissions = table.Column<int>(type: "integer", nullable: false),
                    GrantedPermissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpWorkspaceUserSettings", x => new { x.WorkspaceId, x.UserId });
                    table.ForeignKey(
                        name: "FK_McpWorkspaceUserSettings_WorkspaceMembers_WorkspaceId_UserId",
                        columns: x => new { x.WorkspaceId, x.UserId },
                        principalTable: "WorkspaceMembers",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpUserSettings");

            migrationBuilder.DropTable(
                name: "McpWorkspaceSettings");

            migrationBuilder.DropTable(
                name: "McpWorkspaceUserSettings");

        }
    }
}
