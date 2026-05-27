use sqlx::{migrate::MigrateDatabase, sqlite::SqlitePoolOptions, Sqlite, SqlitePool};
use std::sync::OnceLock;

use crate::types::{McpServer, ServerTransport};

static DB_POOL: OnceLock<SqlitePool> = OnceLock::new();

/// Get a reference to the global database pool.
pub fn pool() -> &'static SqlitePool {
    DB_POOL
        .get()
        .expect("Database pool not initialized. Call init_db() first.")
}

/// Initialize the database: create if needed, run migrations, store the pool.
pub async fn init_db() -> Result<SqlitePool, sqlx::Error> {
    let database_url =
        std::env::var("DATABASE_URL").unwrap_or_else(|_| "sqlite:proxy.db?mode=rwc".to_string());

    // Create database if it doesn't exist
    if !Sqlite::database_exists(&database_url)
        .await
        .unwrap_or(false)
    {
        Sqlite::create_database(&database_url).await?;
    }

    let pool = SqlitePoolOptions::new()
        .max_connections(5)
        .connect(&database_url)
        .await?;

    // Run migrations
    sqlx::migrate!("./migrations").run(&pool).await?;

    DB_POOL
        .set(pool.clone())
        .expect("DB pool already initialized");

    Ok(pool)
}

/// Get all configured servers.
pub async fn get_all_servers(pool: &SqlitePool) -> Result<Vec<McpServer>, sqlx::Error> {
    let rows = sqlx::query_as::<_, ServerRow>(
        "SELECT id, slug, name, transport, enabled, url, auth_header, created_at, updated_at FROM servers ORDER BY name"
    )
    .fetch_all(pool)
    .await?;

    Ok(rows.into_iter().map(|r| r.into()).collect())
}

/// Get only enabled servers.
pub async fn get_enabled_servers(pool: &SqlitePool) -> Result<Vec<McpServer>, sqlx::Error> {
    let rows = sqlx::query_as::<_, ServerRow>(
        "SELECT id, slug, name, transport, enabled, url, auth_header, created_at, updated_at FROM servers WHERE enabled = 1 ORDER BY name"
    )
    .fetch_all(pool)
    .await?;

    Ok(rows.into_iter().map(|r| r.into()).collect())
}

/// Get a single server by slug.
pub async fn get_server_by_slug(
    pool: &SqlitePool,
    slug: &str,
) -> Result<Option<McpServer>, sqlx::Error> {
    let row = sqlx::query_as::<_, ServerRow>(
        "SELECT id, slug, name, transport, enabled, url, auth_header, created_at, updated_at FROM servers WHERE slug = ?"
    )
    .bind(slug)
    .fetch_optional(pool)
    .await?;

    Ok(row.map(|r| r.into()))
}

/// Get a single server by id.
pub async fn get_server_by_id(
    pool: &SqlitePool,
    id: &str,
) -> Result<Option<McpServer>, sqlx::Error> {
    let row = sqlx::query_as::<_, ServerRow>(
        "SELECT id, slug, name, transport, enabled, url, auth_header, created_at, updated_at FROM servers WHERE id = ?"
    )
    .bind(id)
    .fetch_optional(pool)
    .await?;

    Ok(row.map(|r| r.into()))
}

/// Create a new server. Returns the created server.
pub async fn create_server(
    pool: &SqlitePool,
    slug: &str,
    name: &str,
    url: Option<&str>,
    auth_header: Option<&str>,
) -> Result<McpServer, sqlx::Error> {
    let id = uuid::Uuid::new_v4().to_string();
    sqlx::query(
        "INSERT INTO servers (id, slug, name, transport, enabled, url, auth_header) VALUES (?, ?, ?, 'remote_http', 1, ?, ?)"
    )
    .bind(&id)
    .bind(slug)
    .bind(name)
    .bind(url)
    .bind(auth_header)
    .execute(pool)
    .await?;

    // Return the newly created server
    get_server_by_id(pool, &id)
        .await
        .map(|opt| opt.expect("Just inserted"))
}

/// Update an existing server by id.
pub async fn update_server(
    pool: &SqlitePool,
    id: &str,
    slug: &str,
    name: &str,
    url: Option<&str>,
    auth_header: Option<&str>,
) -> Result<(), sqlx::Error> {
    sqlx::query(
        "UPDATE servers SET slug = ?, name = ?, url = ?, auth_header = ?, updated_at = datetime('now') WHERE id = ?"
    )
    .bind(slug)
    .bind(name)
    .bind(url)
    .bind(auth_header)
    .bind(id)
    .execute(pool)
    .await?;

    Ok(())
}

/// Delete a server by id.
pub async fn delete_server(pool: &SqlitePool, id: &str) -> Result<(), sqlx::Error> {
    sqlx::query("DELETE FROM servers WHERE id = ?")
        .bind(id)
        .execute(pool)
        .await?;

    Ok(())
}

/// Toggle the enabled state of a server.
pub async fn toggle_server_enabled(pool: &SqlitePool, id: &str) -> Result<bool, sqlx::Error> {
    sqlx::query(
        "UPDATE servers SET enabled = NOT enabled, updated_at = datetime('now') WHERE id = ?",
    )
    .bind(id)
    .execute(pool)
    .await?;

    // Return the new state
    let row = sqlx::query_scalar::<_, bool>("SELECT enabled FROM servers WHERE id = ?")
        .bind(id)
        .fetch_one(pool)
        .await?;

    Ok(row)
}

// Internal row type for sqlx mapping
#[derive(sqlx::FromRow)]
struct ServerRow {
    id: String,
    slug: String,
    name: String,
    transport: String,
    enabled: bool,
    url: Option<String>,
    auth_header: Option<String>,
    created_at: String,
    updated_at: String,
}

impl From<ServerRow> for McpServer {
    fn from(row: ServerRow) -> Self {
        McpServer {
            id: row.id,
            slug: row.slug,
            name: row.name,
            transport: match row.transport.as_str() {
                "stdio" => ServerTransport::Stdio,
                _ => ServerTransport::RemoteHttp,
            },
            enabled: row.enabled,
            url: row.url,
            auth_header: row.auth_header,
            created_at: row.created_at,
            updated_at: row.updated_at,
        }
    }
}
