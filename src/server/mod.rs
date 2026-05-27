pub mod db;
pub mod namespace;
pub mod proxy_handler;
pub mod upstream;

use axum::extract::FromRef;
use leptos::prelude::*;
use sqlx::SqlitePool;

use self::upstream::UpstreamManager;

#[derive(Clone, FromRef)]
pub struct AppState {
    pub pool: SqlitePool,
    pub upstream: UpstreamManager,
    pub leptos_options: LeptosOptions,
}
