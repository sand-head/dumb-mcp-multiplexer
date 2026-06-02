using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DumbMcpMultiplexer.Migrations
{
    /// <inheritdoc />
    public partial class AddToonModeToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "code_mode_toon_enabled",
                table: "profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "code_mode_toon_enabled",
                table: "profiles");
        }
    }
}
