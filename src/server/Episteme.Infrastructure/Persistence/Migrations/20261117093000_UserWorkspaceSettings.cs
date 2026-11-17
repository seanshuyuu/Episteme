using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Episteme.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserWorkspaceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserWorkspaceSettings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefaultWorkspaceDirectory = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PreferencesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWorkspaceSettings", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserWorkspaceSettings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserWorkspaceSettings");
        }
    }
}
