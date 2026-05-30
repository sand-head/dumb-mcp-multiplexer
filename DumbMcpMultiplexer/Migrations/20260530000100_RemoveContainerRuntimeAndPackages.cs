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
            migrationBuilder.DropColumn(
                name: "container_runtime",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "container_packages",
                table: "servers");
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
