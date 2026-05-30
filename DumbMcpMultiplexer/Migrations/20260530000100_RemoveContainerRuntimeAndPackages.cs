using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using DumbMcpMultiplexer.Data;

#nullable disable

namespace DumbMcpMultiplexer.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260530000100_RemoveContainerRuntimeAndPackages")]
    public partial class RemoveContainerRuntimeAndPackages : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite doesn't support DROP COLUMN — use table rebuild.
            migrationBuilder.Sql("""
                CREATE TABLE "servers_new" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_servers" PRIMARY KEY,
                    "slug" TEXT NOT NULL,
                    "name" TEXT NOT NULL,
                    "transport" TEXT NOT NULL DEFAULT 'remote_http',
                    "enabled" INTEGER NOT NULL DEFAULT 1,
                    "url" TEXT NULL,
                    "headers" TEXT NOT NULL DEFAULT '{}',
                    "command" TEXT NULL,
                    "args" TEXT NULL,
                    "env" TEXT NULL,
                    "package_runner" TEXT NULL,
                    "containerfile" TEXT NULL,
                    "container_image" TEXT NULL,
                    "container_mounts" TEXT NOT NULL DEFAULT '[]',
                    "created_at" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "servers_new" (
                    "id", "slug", "name", "transport", "enabled", "url", "headers",
                    "command", "args", "env", "package_runner", "containerfile",
                    "container_image", "container_mounts", "created_at", "updated_at"
                )
                SELECT
                    "id", "slug", "name", "transport", "enabled", "url", "headers",
                    "command", "args", "env", "package_runner", "containerfile",
                    "container_image", "container_mounts", "created_at", "updated_at"
                FROM "servers";
                """);

            migrationBuilder.Sql("""DROP TABLE "servers";""");

            migrationBuilder.Sql("""ALTER TABLE "servers_new" RENAME TO "servers";""");

            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_servers_slug" ON "servers" ("slug");""");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "container_runtime",
                table: "servers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "container_packages",
                table: "servers",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }
    }
}
