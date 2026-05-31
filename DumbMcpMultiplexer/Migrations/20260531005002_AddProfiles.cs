using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DumbMcpMultiplexer.Migrations
{
    /// <inheritdoc />
    public partial class AddProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "profile_servers",
                columns: table => new
                {
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    server_id = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_servers", x => new { x.profile_id, x.server_id });
                    table.ForeignKey(
                        name: "FK_profile_servers_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_servers_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_capabilities",
                columns: table => new
                {
                    profile_id = table.Column<string>(type: "TEXT", nullable: false),
                    server_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_capabilities", x => new { x.profile_id, x.server_id, x.kind, x.name });
                    table.ForeignKey(
                        name: "FK_profile_capabilities_profile_servers_profile_id_server_id",
                        columns: x => new { x.profile_id, x.server_id },
                        principalTable: "profile_servers",
                        principalColumns: new[] { "profile_id", "server_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_profile_servers_server_id",
                table: "profile_servers",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_name",
                table: "profiles",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_capabilities");

            migrationBuilder.DropTable(
                name: "profile_servers");

            migrationBuilder.DropTable(
                name: "profiles");
        }
    }
}
