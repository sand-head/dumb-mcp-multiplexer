use serde::{Deserialize, Serialize};

/// A configured upstream MCP server.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct McpServer {
    pub id: String,
    pub slug: String,
    pub name: String,
    pub transport: ServerTransport,
    pub enabled: bool,
    pub url: Option<String>,
    pub auth_header: Option<String>,
    pub created_at: String,
    pub updated_at: String,
}

/// Transport type for an upstream MCP server.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum ServerTransport {
    RemoteHttp,
    Stdio,
}

impl std::fmt::Display for ServerTransport {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::RemoteHttp => write!(f, "remote_http"),
            Self::Stdio => write!(f, "stdio"),
        }
    }
}
