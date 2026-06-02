# Copilot Instructions

## EF Core Migrations

Always create migrations using the CLI. **Never write migration files by hand.**

```bash
dotnet ef migrations add <MigrationName> --project DumbMcpMultiplexer/DumbMcpMultiplexer.csproj
```

After adding a migration, verify the model is in sync:

```bash
dotnet ef migrations has-pending-model-changes --project DumbMcpMultiplexer/DumbMcpMultiplexer.csproj
```

Hand-written migrations have caused production incidents by skipping the snapshot update step, which causes `PendingModelChangesWarning` to crash the app on startup before any migrations can run.

The only exception is a hotfix/repair migration that needs to be idempotent (e.g. `ALTER TABLE … ADD COLUMN IF NOT EXISTS`). Even then, update `AppDbContext.OnModelCreating` and the snapshot separately with a CLI-generated migration first.
