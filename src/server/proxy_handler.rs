use rmcp::model::*;
use rmcp::service::RequestContext;
use rmcp::{ErrorData, RoleServer, ServerHandler};

use super::namespace;
use super::upstream::UpstreamManager;

/// The MCP proxy handler that aggregates multiple upstream servers.
#[derive(Clone)]
pub struct ProxyHandler {
    pub upstream: UpstreamManager,
}

impl ProxyHandler {
    pub fn new(upstream: UpstreamManager) -> Self {
        Self { upstream }
    }
}

impl ServerHandler for ProxyHandler {
    fn get_info(&self) -> ServerInfo {
        let mut info = ServerInfo::default();
        info.instructions = Some(
            "MCP Proxy — aggregates tools, resources, and prompts from multiple upstream MCP servers."
                .into(),
        );
        info.server_info = Implementation::new("dumb-mcp-server-proxy", env!("CARGO_PKG_VERSION"));
        info.capabilities = ServerCapabilities::builder()
            .enable_tools()
            .enable_resources()
            .enable_prompts()
            .build();
        info
    }

    async fn list_tools(
        &self,
        _request: Option<PaginatedRequestParams>,
        _context: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, ErrorData> {
        let mut all_tools: Vec<Tool> = Vec::new();

        for entry in self.upstream.connections.iter() {
            let slug = entry.key();
            let client = entry.value();

            match client.peer().list_all_tools().await {
                Ok(tools) => {
                    for mut tool in tools {
                        tool.name = namespace::prefix(slug, &tool.name).into();
                        all_tools.push(tool);
                    }
                }
                Err(e) => {
                    tracing::warn!(server = %slug, error = ?e, "Failed to list tools from upstream");
                }
            }
        }

        let mut result = ListToolsResult::default();
        result.tools = all_tools;
        Ok(result)
    }

    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<CallToolResult, ErrorData> {
        let (slug, real_name) = namespace::split(&request.name).ok_or_else(|| {
            ErrorData::invalid_params(
                format!(
                    "Tool name '{}' is missing namespace prefix (expected format: slug__tool_name)",
                    request.name
                ),
                None::<serde_json::Value>,
            )
        })?;

        let client = self.upstream.connections.get(slug).ok_or_else(|| {
            ErrorData::invalid_params(
                format!("No upstream server with slug '{slug}'"),
                None::<serde_json::Value>,
            )
        })?;

        let mut params = CallToolRequestParams::new(real_name.to_string());
        if let Some(args) = request.arguments {
            params = params.with_arguments(args);
        }

        client.peer().call_tool(params).await.map_err(|e| {
            ErrorData::internal_error(
                format!("Upstream '{slug}' error: {e}"),
                None::<serde_json::Value>,
            )
        })
    }

    async fn list_resources(
        &self,
        _request: Option<PaginatedRequestParams>,
        _context: RequestContext<RoleServer>,
    ) -> Result<ListResourcesResult, ErrorData> {
        let mut all_resources: Vec<Resource> = Vec::new();

        for entry in self.upstream.connections.iter() {
            let slug = entry.key();
            let client = entry.value();

            match client.peer().list_all_resources().await {
                Ok(resources) => {
                    for mut resource in resources {
                        resource.uri = namespace::prefix_uri(slug, &resource.uri).into();
                        resource.name = namespace::prefix(slug, &resource.name).into();
                        all_resources.push(resource);
                    }
                }
                Err(e) => {
                    tracing::warn!(server = %slug, error = ?e, "Failed to list resources from upstream");
                }
            }
        }

        let mut result = ListResourcesResult::default();
        result.resources = all_resources;
        Ok(result)
    }

    async fn read_resource(
        &self,
        request: ReadResourceRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<ReadResourceResult, ErrorData> {
        let (slug, real_uri) = namespace::split_uri(&request.uri).ok_or_else(|| {
            ErrorData::invalid_params(
                format!(
                    "Resource URI '{}' is missing namespace prefix (expected format: proxy://slug/uri)",
                    request.uri
                ),
                None::<serde_json::Value>,
            )
        })?;

        let client = self.upstream.connections.get(slug).ok_or_else(|| {
            ErrorData::invalid_params(
                format!("No upstream server with slug '{slug}'"),
                None::<serde_json::Value>,
            )
        })?;

        let params = ReadResourceRequestParams::new(real_uri.to_string());

        client.peer().read_resource(params).await.map_err(|e| {
            ErrorData::internal_error(
                format!("Upstream '{slug}' error: {e}"),
                None::<serde_json::Value>,
            )
        })
    }

    async fn list_prompts(
        &self,
        _request: Option<PaginatedRequestParams>,
        _context: RequestContext<RoleServer>,
    ) -> Result<ListPromptsResult, ErrorData> {
        let mut all_prompts: Vec<Prompt> = Vec::new();

        for entry in self.upstream.connections.iter() {
            let slug = entry.key();
            let client = entry.value();

            match client.peer().list_all_prompts().await {
                Ok(prompts) => {
                    for mut prompt in prompts {
                        prompt.name = namespace::prefix(slug, &prompt.name).into();
                        all_prompts.push(prompt);
                    }
                }
                Err(e) => {
                    tracing::warn!(server = %slug, error = ?e, "Failed to list prompts from upstream");
                }
            }
        }

        let mut result = ListPromptsResult::default();
        result.prompts = all_prompts;
        Ok(result)
    }

    async fn get_prompt(
        &self,
        request: GetPromptRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<GetPromptResult, ErrorData> {
        let (slug, real_name) = namespace::split(&request.name).ok_or_else(|| {
            ErrorData::invalid_params(
                format!(
                    "Prompt name '{}' is missing namespace prefix (expected format: slug__prompt_name)",
                    request.name
                ),
                None::<serde_json::Value>,
            )
        })?;

        let client = self.upstream.connections.get(slug).ok_or_else(|| {
            ErrorData::invalid_params(
                format!("No upstream server with slug '{slug}'"),
                None::<serde_json::Value>,
            )
        })?;

        let mut params = GetPromptRequestParams::new(real_name.to_string());
        if let Some(args) = request.arguments {
            params = params.with_arguments(args);
        }

        client.peer().get_prompt(params).await.map_err(|e| {
            ErrorData::internal_error(
                format!("Upstream '{slug}' error: {e}"),
                None::<serde_json::Value>,
            )
        })
    }
}
