using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Episteme.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceDirectorySkeleton : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvisioningDirectoriesJson",
                table: "Workspaces",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvisioningDirectoriesJson",
                table: "Workspaces");
        }
    }
}
