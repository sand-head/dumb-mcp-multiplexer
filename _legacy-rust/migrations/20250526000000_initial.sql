-- Configured upstream MCP servers
CREATE TABLE IF NOT EXISTS servers (
    id          TEXT PRIMARY KEY,
    slug        TEXT NOT NULL UNIQUE,
    name        TEXT NOT NULL,
    transport   TEXT NOT NULL DEFAULT 'remote_http',
    enabled     INTEGER NOT NULL DEFAULT 1,
    url         TEXT,
    auth_header TEXT,
    command     TEXT,
    args        TEXT,
    env         TEXT,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Cache of discovered capabilities per server
CREATE TABLE IF NOT EXISTS server_capabilities (
    server_id   TEXT NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    kind        TEXT NOT NULL,
    name        TEXT NOT NULL,
    description TEXT,
    schema_json TEXT,
    fetched_at  TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (server_id, kind, name)
);

-- General app settings
CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
