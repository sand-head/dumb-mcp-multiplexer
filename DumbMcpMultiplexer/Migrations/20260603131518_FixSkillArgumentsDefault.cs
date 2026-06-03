using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DumbMcpMultiplexer.Migrations
{
    /// <inheritdoc />
    public partial class FixSkillArgumentsDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "arguments_json",
                table: "skills",
                type: "TEXT",
                nullable: false,
                defaultValueSql: "'[]'",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "arguments_json",
                table: "skills",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValueSql: "'[]'");
        }
    }
}
