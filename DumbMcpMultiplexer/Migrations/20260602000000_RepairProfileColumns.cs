using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DumbMcpMultiplexer.Migrations
{
    /// <summary>
    /// Idempotent repair migration: ensures code_mode_enabled and code_mode_toon_enabled
    /// columns exist on the profiles table regardless of whether the earlier migrations
    /// (AddCodeModeToProfile, AddToonModeToProfile) were actually applied to this database
    /// instance. Uses IF NOT EXISTS, supported by SQLite 3.37+ (bundled with
    /// Microsoft.Data.Sqlite 6+).
    /// </summary>
    public partial class RepairProfileColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE profiles ADD COLUMN IF NOT EXISTS code_mode_enabled INTEGER NOT NULL DEFAULT 0");
            migrationBuilder.Sql(
                "ALTER TABLE profiles ADD COLUMN IF NOT EXISTS code_mode_toon_enabled INTEGER NOT NULL DEFAULT 0");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite does not support DROP COLUMN before 3.35.0 and these columns
            // are load-bearing, so we intentionally leave them in place on rollback.
        }
    }
}
