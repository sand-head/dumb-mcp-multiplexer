#[cfg(feature = "ssr")]
#[tokio::main]
async fn main() {
    use axum::{middleware, Router};
    use dumb_mcp_server_proxy::app::*;
    use dumb_mcp_server_proxy::server::{
        db, proxy_handler::ProxyHandler, upstream::UpstreamManager, AllowedHosts,
    };
    use leptos::prelude::*;
    use leptos_axum::{generate_route_list, LeptosRoutes};
    use rmcp::transport::streamable_http_server::{
        session::local::LocalSessionManager, StreamableHttpServerConfig, StreamableHttpService,
    };
    use std::sync::Arc;
    use tokio::sync::RwLock;
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

    // Load allowed hosts from settings DB
    let hosts_setting = db::get_setting(&pool, "allowed_hosts")
        .await
        .ok()
        .flatten()
        .unwrap_or_default();
    let initial_hosts: Vec<String> = hosts_setting
        .split(',')
        .map(|h| h.trim().to_string())
        .filter(|h| !h.is_empty())
        .collect();
    let allowed_hosts: AllowedHosts = Arc::new(RwLock::new(initial_hosts));
    dumb_mcp_server_proxy::server::init_allowed_hosts(allowed_hosts.clone());

    // Leptos configuration
    let conf = get_configuration(None).unwrap();
    let addr = conf.leptos_options.site_addr;
    let leptos_options = conf.leptos_options;

    // Build application state
    let app_state = dumb_mcp_server_proxy::server::AppState {
        pool: pool.clone(),
        upstream: upstream.clone(),
        leptos_options: leptos_options.clone(),
        allowed_hosts: allowed_hosts.clone(),
    };

    // Generate Leptos routes
    let routes = generate_route_list(App);

    // Build the MCP proxy service (tower-compatible)
    // Disable rmcp's built-in host check — we handle it dynamically via middleware.
    let session_manager = Arc::new(LocalSessionManager::default());
    let upstream_for_mcp = upstream.clone();
    let mcp_config = StreamableHttpServerConfig::default().disable_allowed_hosts();

    let mcp_service = StreamableHttpService::new(
        move || Ok(ProxyHandler::new(upstream_for_mcp.clone())),
        session_manager,
        mcp_config,
    );

    // Build the router:
    // 1. MCP proxy at /mcp (with dynamic host validation middleware)
    // 2. Leptos SSR routes (web UI)
    let app = Router::new()
        .nest_service("/mcp", mcp_service)
        .layer(middleware::from_fn(host_guard))
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

/// Middleware that validates the Host header against the dynamic allowed-hosts list.
/// Applied only to the /mcp route via layering.
#[cfg(feature = "ssr")]
async fn host_guard(
    req: axum::http::Request<axum::body::Body>,
    next: axum::middleware::Next,
) -> axum::response::Response {
    let host = req
        .headers()
        .get(axum::http::header::HOST)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");

    // Normalize: strip port, lowercase
    let host_name = host
        .rsplit_once(':')
        .map(|(h, _)| h)
        .unwrap_or(host)
        .trim_matches(|c| c == '[' || c == ']')
        .to_ascii_lowercase();

    let allowed_hosts = dumb_mcp_server_proxy::server::allowed_hosts();
    let hosts = allowed_hosts.read().await;

    // If the list is empty, only allow loopback (default safe behavior)
    if hosts.is_empty() {
        let is_loopback =
            host_name == "localhost" || host_name == "127.0.0.1" || host_name == "::1";
        drop(hosts);
        if is_loopback {
            return next.run(req).await;
        } else {
            tracing::warn!(
                host = %host,
                "Rejected request with disallowed Host header"
            );
            return axum::response::Response::builder()
                .status(axum::http::StatusCode::FORBIDDEN)
                .body(axum::body::Body::from(
                    "Forbidden: Host not allowed. Configure allowed hosts in Settings.",
                ))
                .unwrap();
        }
    }

    // Always allow loopback in addition to the configured list
    let is_loopback = host_name == "localhost" || host_name == "127.0.0.1" || host_name == "::1";
    let is_allowed = is_loopback
        || hosts.iter().any(|allowed| {
            let allowed_lower = allowed.to_ascii_lowercase();
            // Match with or without port
            if let Some((allowed_host, _port)) = allowed_lower.rsplit_once(':') {
                allowed_host == host_name || allowed_lower == host.to_ascii_lowercase()
            } else {
                allowed_lower == host_name
            }
        });

    drop(hosts);

    if is_allowed {
        next.run(req).await
    } else {
        tracing::warn!(
            host = %host,
            "Rejected request with disallowed Host header"
        );
        axum::response::Response::builder()
            .status(axum::http::StatusCode::FORBIDDEN)
            .body(axum::body::Body::from(
                "Forbidden: Host not allowed. Configure allowed hosts in Settings.",
            ))
            .unwrap()
    }
}

#[cfg(not(feature = "ssr"))]
pub fn main() {
    // no client-side main function
}
