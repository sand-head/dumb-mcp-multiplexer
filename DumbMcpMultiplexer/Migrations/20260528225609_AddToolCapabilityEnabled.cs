using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DumbMcpMultiplexer.Migrations
{
    /// <inheritdoc />
    public partial class AddToolCapabilityEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enabled",
                table: "server_capabilities",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enabled",
                table: "server_capabilities");
        }
    }
}
