use dashmap::DashMap;
use reqwest::header::{HeaderName, HeaderValue};
use rmcp::transport::streamable_http_client::{
    StreamableHttpClientTransportConfig, StreamableHttpClientWorker,
};
use rmcp::{service::RunningService, RoleClient, ServiceExt};
use sqlx::SqlitePool;
use std::collections::HashMap;
use std::sync::Arc;

use super::db;

/// Type alias for a running MCP client connection.
pub type ClientSession = RunningService<RoleClient, ()>;

/// Manages active connections to upstream MCP servers.
#[derive(Clone)]
pub struct UpstreamManager {
    /// slug → active client session
    pub connections: Arc<DashMap<String, ClientSession>>,
}

impl UpstreamManager {
    pub fn new() -> Self {
        Self {
            connections: Arc::new(DashMap::new()),
        }
    }

    /// Connect to a single upstream MCP server. Returns Ok(()) on success.
    /// This performs the full MCP initialize handshake.
    pub async fn connect(
        &self,
        slug: &str,
        url: &str,
        headers: &HashMap<String, String>,
    ) -> Result<(), String> {
        tracing::info!(slug = %slug, url = %url, "Connecting to upstream MCP server");
        let config = build_transport_config(url, headers)?;

        // Create the transport worker (reqwest-based)
        let transport = StreamableHttpClientWorker::new(reqwest::Client::new(), config);

        // Serve initiates the MCP handshake (initialize + initialized notification)
        let client: ClientSession =
            ().serve(transport)
                .await
                .map_err(|e| format!("Failed to connect: {e}"))?;

        // Store the active connection
        self.connections.insert(slug.to_string(), client);
        tracing::info!(slug = %slug, "Successfully connected to upstream");
        Ok(())
    }

    /// Disconnect from an upstream server (drop the connection).
    pub async fn disconnect(&self, slug: &str) {
        if let Some((_, session)) = self.connections.remove(slug) {
            let _ = session.cancel().await;
        }
    }

    /// Test a connection to an upstream server without storing it.
    /// Returns the server info and tool count on success.
    pub async fn test_connection(
        url: &str,
        headers: &HashMap<String, String>,
    ) -> Result<ConnectionTestResult, String> {
        let config = build_transport_config(url, headers)?;

        let transport = StreamableHttpClientWorker::new(reqwest::Client::new(), config);

        let client: ClientSession =
            ().serve(transport)
                .await
                .map_err(|e| format!("Connection failed: {e}"))?;

        // Get server info
        let server_info = client.peer_info().cloned();

        // Try listing tools
        let tools = client
            .peer()
            .list_all_tools()
            .await
            .map_err(|e| format!("Failed to list tools: {e}"))?;

        let tool_count = tools.len();
        let tool_names: Vec<String> = tools.into_iter().map(|t| t.name.to_string()).collect();

        // Clean up
        let _ = client.cancel().await;

        Ok(ConnectionTestResult {
            server_name: server_info
                .as_ref()
                .map(|i| i.server_info.name.clone())
                .unwrap_or_else(|| "Unknown".to_string()),
            server_version: server_info
                .as_ref()
                .map(|i| i.server_info.version.clone())
                .unwrap_or_else(|| "Unknown".to_string()),
            tool_count,
            tool_names,
        })
    }

    /// Synchronize connections based on the current database state.
    /// Connects to newly enabled servers, disconnects removed/disabled ones.
    pub async fn sync(&self, pool: &SqlitePool) {
        let servers = db::get_enabled_servers(pool).await.unwrap_or_default();
        let desired_slugs: std::collections::HashSet<String> =
            servers.iter().map(|s| s.slug.clone()).collect();

        // Remove connections for disabled/deleted servers
        let current_slugs: Vec<String> = self.connections.iter().map(|e| e.key().clone()).collect();

        for slug in current_slugs {
            if !desired_slugs.contains(&slug) {
                self.disconnect(&slug).await;
            }
        }

        // Add connections for new/re-enabled servers
        for server in &servers {
            if !self.connections.contains_key(&server.slug) {
                if let Some(url) = &server.url {
                    if let Err(e) = self.connect(&server.slug, url, &server.headers).await {
                        tracing::error!(slug = %server.slug, error = %e, "Failed to connect to upstream");
                    }
                }
            }
        }
    }
}

/// Build a transport config from a URL and headers map.
fn build_transport_config(
    url: &str,
    headers: &HashMap<String, String>,
) -> Result<StreamableHttpClientTransportConfig, String> {
    let mut config = StreamableHttpClientTransportConfig::with_uri(url);

    let mut custom_headers = HashMap::new();
    for (key, value) in headers {
        let header_name = HeaderName::from_bytes(key.as_bytes())
            .map_err(|e| format!("Invalid header name '{key}': {e}"))?;
        let header_value = HeaderValue::from_str(value)
            .map_err(|e| format!("Invalid header value for '{key}': {e}"))?;
        custom_headers.insert(header_name, header_value);
    }
    config.custom_headers = custom_headers;

    Ok(config)
}

/// Result of a connection test.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct ConnectionTestResult {
    pub server_name: String,
    pub server_version: String,
    pub tool_count: usize,
    pub tool_names: Vec<String>,
}
