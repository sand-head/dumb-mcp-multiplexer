#[cfg(feature = "ssr")]
#[tokio::main]
async fn main() {
    use axum::Router;
    use dumb_mcp_server_proxy::app::*;
    use dumb_mcp_server_proxy::server::{
        db, proxy_handler::ProxyHandler, upstream::UpstreamManager,
    };
    use leptos::prelude::*;
    use leptos_axum::{generate_route_list, LeptosRoutes};
    use rmcp::transport::streamable_http_server::{
        session::local::LocalSessionManager, StreamableHttpService,
    };
    use std::sync::Arc;
    use tracing_subscriber::EnvFilter;

    // Initialize tracing
    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::from_default_env().add_directive("info".parse().unwrap()))
        .init();

    // Initialize database
    let pool = db::init_db().await.expect("Failed to initialize database");
    tracing::info!("Database initialized");

    // Initialize upstream connection manager and sync with DB
    let upstream = UpstreamManager::new();
    upstream.sync(&pool).await;
    tracing::info!("Upstream connections synced");

    // Leptos configuration
    let conf = get_configuration(None).unwrap();
    let addr = conf.leptos_options.site_addr;
    let leptos_options = conf.leptos_options;

    // Build application state
    let app_state = dumb_mcp_server_proxy::server::AppState {
        pool: pool.clone(),
        upstream: upstream.clone(),
        leptos_options: leptos_options.clone(),
    };

    // Generate Leptos routes
    let routes = generate_route_list(App);

    // Build the MCP proxy service (tower-compatible)
    let session_manager = Arc::new(LocalSessionManager::default());
    let upstream_for_mcp = upstream.clone();
    let mcp_service = StreamableHttpService::new(
        move || Ok(ProxyHandler::new(upstream_for_mcp.clone())),
        session_manager,
        Default::default(),
    );

    // Build the router:
    // 1. MCP proxy at /mcp
    // 2. Leptos SSR routes (web UI)
    let app = Router::new()
        .nest_service("/mcp", mcp_service)
        .leptos_routes(&app_state, routes, {
            let leptos_options = leptos_options.clone();
            move || shell(leptos_options.clone())
        })
        .fallback(leptos_axum::file_and_error_handler::<
            dumb_mcp_server_proxy::server::AppState,
            _,
        >(shell))
        .with_state(app_state);

    tracing::info!("Listening on http://{}", &addr);
    tracing::info!("MCP endpoint available at POST http://{}/mcp", &addr);
    let listener = tokio::net::TcpListener::bind(&addr).await.unwrap();
    axum::serve(listener, app.into_make_service())
        .await
        .unwrap();
}

#[cfg(not(feature = "ssr"))]
pub fn main() {
    // no client-side main function
}
