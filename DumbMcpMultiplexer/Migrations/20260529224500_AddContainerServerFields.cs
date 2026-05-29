using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using DumbMcpMultiplexer.Data;

#nullable disable

namespace DumbMcpMultiplexer.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260529224500_AddContainerServerFields")]
    /// <inheritdoc />
    public partial class AddContainerServerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "container_image",
                table: "servers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "container_mounts",
                table: "servers",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "container_packages",
                table: "servers",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "container_runtime",
                table: "servers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "container_image",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "container_mounts",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "container_packages",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "container_runtime",
                table: "servers");
        }
    }
}
