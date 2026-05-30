using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using DumbMcpMultiplexer.Data;

#nullable disable

namespace DumbMcpMultiplexer.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260530000000_AddPackageRunnerFields")]
    public partial class AddPackageRunnerFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "package_runner",
                table: "servers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "containerfile",
                table: "servers",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "package_runner",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "containerfile",
                table: "servers");
        }
    }
}
