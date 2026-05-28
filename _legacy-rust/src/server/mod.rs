pub mod db;
pub mod namespace;
pub mod proxy_handler;
pub mod upstream;

use axum::extract::FromRef;
use leptos::prelude::*;
use sqlx::SqlitePool;
use std::sync::{Arc, OnceLock};
use tokio::sync::RwLock;

use self::upstream::UpstreamManager;

/// Shared list of allowed MCP host headers, dynamically updatable from the settings page.
pub type AllowedHosts = Arc<RwLock<Vec<String>>>;

static ALLOWED_HOSTS: OnceLock<AllowedHosts> = OnceLock::new();

/// Initialize the global allowed hosts state.
pub fn init_allowed_hosts(hosts: AllowedHosts) {
    ALLOWED_HOSTS
        .set(hosts)
        .expect("Allowed hosts already initialized");
}

/// Get a reference to the global allowed hosts state.
pub fn allowed_hosts() -> &'static AllowedHosts {
    ALLOWED_HOSTS
        .get()
        .expect("Allowed hosts not initialized. Call init_allowed_hosts() first.")
}

#[derive(Clone, FromRef)]
pub struct AppState {
    pub pool: SqlitePool,
    pub upstream: UpstreamManager,
    pub leptos_options: LeptosOptions,
    pub allowed_hosts: AllowedHosts,
}
