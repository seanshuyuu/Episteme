using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Episteme.Infrastructure.Persistence.Migrations;

[Migration("20261119161000_ProjectHierarchy")]
public partial class ProjectHierarchy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "Projects" (
    "Id" uuid NOT NULL,
    "WorkspaceId" uuid NOT NULL,
    "CreatedByUserId" uuid NOT NULL,
    "Name" character varying(120) NOT NULL,
    "Slug" character varying(80) NOT NULL,
    "Description" character varying(2000),
    "StorageKey" character varying(32) NOT NULL,
    "DefaultBranch" character varying(255) NOT NULL,
    "Status" character varying(32) NOT NULL,
    "ConcurrencyStamp" uuid NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Projects" PRIMARY KEY ("Id")
);
""");
        migrationBuilder.Sql("""
ALTER TABLE "Projects"
ADD CONSTRAINT "FK_Projects_Workspaces_WorkspaceId"
FOREIGN KEY ("WorkspaceId") REFERENCES "Workspaces"("Id") ON DELETE RESTRICT;
""");
        migrationBuilder.Sql("""
ALTER TABLE "Projects"
ADD CONSTRAINT "FK_Projects_AspNetUsers_CreatedByUserId"
FOREIGN KEY ("CreatedByUserId") REFERENCES "AspNetUsers"("Id") ON DELETE RESTRICT;
""");
        migrationBuilder.Sql("""
CREATE UNIQUE INDEX "IX_Projects_Slug" ON "Projects" ("WorkspaceId", "Slug");
CREATE UNIQUE INDEX "IX_Projects_StorageKey" ON "Projects" ("StorageKey");
CREATE INDEX "IX_Projects_WorkspaceId" ON "Projects" ("WorkspaceId");
CREATE INDEX "IX_Projects_CreatedByUserId" ON "Projects" ("CreatedByUserId");
""");
        migrationBuilder.Sql("""
INSERT INTO "Projects"
("Id","WorkspaceId","CreatedByUserId","Name","Slug","Description","StorageKey","DefaultBranch","Status","ConcurrencyStamp","CreatedAt","UpdatedAt")
SELECT w."Id", w."Id", w."CreatedByUserId", w."Name", w."Slug", w."Description",
       w."StorageKey", w."DefaultBranch",
       CASE WHEN w."Status" = 'Archived' THEN 'Archived' ELSE 'Working' END,
       gen_random_uuid(), w."CreatedAt", w."UpdatedAt"
FROM "Workspaces" w
WHERE NOT EXISTS (
    SELECT 1 FROM "Projects" p WHERE p."Id" = w."Id"
);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "Projects";""");
    }
}
