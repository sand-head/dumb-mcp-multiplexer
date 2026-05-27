# dumb-mcp-server-proxy — Design Plan

## Overview

A Docker-containerized **Leptos + Axum** SSR web app that:

1. Provides a web UI for configuring multiple upstream MCP (Model Context Protocol) servers
2. Exposes a **single MCP-compliant endpoint** that aggregates and proxies all configured backends
3. Currently targets **remote (HTTP) MCP servers**, with a clear path to **stdio-based MCPs** later

Think of it as an MCP multiplexer — your AI client connects to one endpoint and gets
tools/resources/prompts from all your configured MCP servers merged together.

---

## Why "Dumb"?

No intelligence, no filtering, no orchestration. It simply:
- Concatenates `tools/list` results from all backends (namespaced to avoid collisions)
- Routes `tools/call` to the correct backend based on the tool's namespace prefix
- Same for resources and prompts
- Passes through errors transparently

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  AI Client (Claude, Cursor, etc.)                           │
│  Connects to: POST http://proxy:3000/mcp                    │
└────────────────────────┬────────────────────────────────────┘
                         │ MCP (Streamable HTTP)
                         ▼
┌─────────────────────────────────────────────────────────────┐
│  dumb-mcp-server-proxy                                      │
│                                                             │
│  ┌─────────────────┐   ┌─────────────────────────────────┐ │
│  │  Web UI (Leptos) │   │  MCP Proxy Endpoint (/mcp)      │ │
│  │  - Add/edit/rm   │   │  - Handles initialize           │ │
│  │    servers        │   │  - Aggregates tools/list        │ │
│  │  - View status   │   │  - Routes tools/call            │ │
│  │  - Test conn.    │   │  - Aggregates resources/list    │ │
│  └────────┬─────────┘   │  - Routes resources/read        │ │
│           │              │  - Aggregates prompts/list      │ │
│           │              │  - Routes prompts/get           │ │
│           ▼              └──────────┬──────────────────────┘ │
│  ┌─────────────────┐               │                        │
│  │  SQLite (config) │               │                        │
│  │  - servers table │               │                        │
│  │  - settings      │               │                        │
│  └─────────────────┘               │                        │
└─────────────────────────────────────┼────────────────────────┘
                                      │ Fan-out
                    ┌─────────────────┼─────────────────┐
                    ▼                 ▼                  ▼
            ┌─────────────┐  ┌─────────────┐   ┌─────────────┐
            │ MCP Server A │  │ MCP Server B │   │ MCP Server C │
            │ (remote HTTP)│  │ (remote HTTP)│   │ (future:stdio)│
            └─────────────┘  └─────────────┘   └─────────────┘
```

---

## MCP Protocol Primer (for context)

MCP uses **JSON-RPC 2.0** over HTTP. The modern transport is "Streamable HTTP":

- Client sends `POST /mcp` with JSON-RPC request body
- Server responds with JSON-RPC response (or SSE stream for streaming)
- Session management via `Mcp-Session-Id` header

Key methods the proxy needs to handle:

| Method | Proxy Behavior |
|--------|----------------|
| `initialize` | Respond directly with aggregated capabilities |
| `notifications/initialized` | Forward to all backends |
| `tools/list` | Fan-out to all backends, merge results with namespace prefix |
| `tools/call` | Route to correct backend based on tool name prefix |
| `resources/list` | Fan-out, merge with prefix |
| `resources/read` | Route to correct backend |
| `resources/templates/list` | Fan-out, merge with prefix |
| `prompts/list` | Fan-out, merge with prefix |
| `prompts/get` | Route to correct backend |
| `ping` | Respond directly |

### Namespacing Strategy

To avoid tool/resource name collisions across backends, prefix with the server's
configured slug:

```
Backend "github" with tool "create_issue"  →  exposed as "github__create_issue"
Backend "slack"  with tool "send_message"  →  exposed as "slack__send_message"
```

The double-underscore delimiter (`__`) is used for routing: when `tools/call` comes
in for `"github__create_issue"`, strip the prefix and forward `"create_issue"` to
the "github" backend.

---

## Tech Stack

| Layer | Technology | Rationale |
|-------|-----------|----------|
| Framework | **Leptos 0.8** | Full-stack Rust, SSR + hydration (matches Hearr) |
| Server | **Axum** (via leptos_axum) | Async, tower ecosystem, shared types |
| MCP SDK | **rmcp 1.7** | Official Rust MCP SDK — handles protocol, types, transport |
| Styling | **Tailwind CSS** | cargo-leptos built-in support, quick iteration |
| Database | **SQLite** (via sqlx) | Config persistence, single-file, no ext. deps |
| Build | **cargo-leptos** | SSR binary + WASM client + Tailwind in one step |
| Container | **Docker** (multi-stage) | Same pattern as Hearr's Dockerfile |

### rmcp Feature Flags

```toml
# In Cargo.toml under [dependencies] (ssr feature-gated)
rmcp = { version = "1.7", features = [
    # Server side — we present ourselves as an MCP server to the AI client
    "server",
    "transport-streamable-http-server",  # Streamable HTTP transport (POST /mcp)
    "tower",                             # Tower Service — plugs directly into Axum router

    # Client side — we connect to upstream MCP servers
    "client",
    "transport-streamable-http-client-reqwest",  # HTTP client via reqwest

    # Future: stdio subprocess MCP servers
    # "transport-child-process",
] }
```

**What rmcp gives us for free:**
- All JSON-RPC 2.0 types (request, response, error, notifications)
- All MCP model types (Tool, Resource, Prompt, ServerCapabilities, etc.)
- `ServerHandler` trait — we implement this for our proxy aggregator
- `tower::Service` integration — the MCP server becomes an Axum-compatible service
- Streamable HTTP transport with SSE support and session management
- Client SDK (`list_tools`, `call_tool`, `list_resources`, etc.) for upstream calls
- Proper `Mcp-Session-Id` handling on both sides

---

## Data Model

### SQLite Schema

```sql
-- Configured upstream MCP servers
CREATE TABLE servers (
    id          TEXT PRIMARY KEY,        -- UUID
    slug        TEXT NOT NULL UNIQUE,    -- url-safe identifier, used as namespace prefix
    name        TEXT NOT NULL,           -- human-friendly display name
    transport   TEXT NOT NULL DEFAULT 'remote_http',  -- 'remote_http' | 'stdio' (future)
    enabled     INTEGER NOT NULL DEFAULT 1,

    -- Remote HTTP fields
    url         TEXT,                    -- base URL of the remote MCP server
    auth_header TEXT,                    -- optional Authorization header value (encrypted at rest?)

    -- Stdio fields (future)
    command     TEXT,                    -- e.g. "npx" or "/usr/local/bin/my-mcp"
    args        TEXT,                    -- JSON array of args, e.g. '["@modelcontextprotocol/server-github"]'
    env         TEXT,                    -- JSON object of env vars, e.g. '{"GITHUB_TOKEN":"..."}'

    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Optional: cache discovered capabilities per server (avoids re-fetching on every list)
CREATE TABLE server_capabilities (
    server_id   TEXT NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    kind        TEXT NOT NULL,           -- 'tool' | 'resource' | 'resource_template' | 'prompt'
    name        TEXT NOT NULL,           -- original (un-prefixed) name
    description TEXT,
    schema_json TEXT,                    -- JSON schema for tool input / resource, etc.
    fetched_at  TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (server_id, kind, name)
);

-- General app settings (single row)
CREATE TABLE settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

---

## Project Structure

```
dumb-mcp-server-proxy/
├── Cargo.toml
├── Cargo.lock
├── Dockerfile
├── compose.yml
├── PLAN.md                          ← You are here
├── style/
│   ├── main.scss
│   └── tailwind.css                 # Tailwind v4 entry point
├── public/                          # Static assets
├── migrations/
│   └── 20250526000000_initial.sql
└── src/
    ├── main.rs                      # Server entry (Axum), tracing, DB init
    ├── lib.rs                       # Hydration entry, module declarations
    ├── app.rs                       # Root component, routing, shell
    ├── types.rs                     # Shared types (McpServer, ServerTransport)
    │
    ├── components/
    │   ├── mod.rs
    │   ├── layout.rs               # App shell (header, nav, footer)
    │   └── server_form.rs          # Add/edit server modal form
    │
    ├── pages/
    │   ├── mod.rs
    │   ├── dashboard.rs            # Server grid, "Add Server" button
    │   └── server_detail.rs        # Config view, edit/delete/toggle actions
    │
    └── server/                      # Server-only (#[cfg(feature = "ssr")])
        ├── mod.rs                  # AppState
        ├── db.rs                   # SQLite pool, CRUD queries
        ├── proxy_handler.rs        # (Phase 3) ServerHandler impl
        ├── upstream.rs             # (Phase 3) rmcp client connections
        └── namespace.rs            # (Phase 3) Slug-based prefixing/routing
```

---

## Key Design Decisions

### 1. Persistent client connections via rmcp

**Decision: Use rmcp's client SDK with persistent connections from the start.**

Since `rmcp` manages client sessions natively, we maintain a pool of active client
connections to upstream servers in `Arc<DashMap<ServerId, RunningService<...>>>`. When
a server is enabled, we `initialize` a client session and keep it alive. When disabled,
we drop it. This is simple with rmcp — it's how the SDK is designed to be used.

The `server_capabilities` cache still makes sense to avoid re-fetching on every
`tools/list` from the AI client (we refresh on connect + manual "Refresh" button).

### 2. Session handling (handled by rmcp)

The `transport-streamable-http-server` feature handles downstream session management
automatically (`Mcp-Session-Id` generation, session routing, etc.). Upstream sessions
are managed by rmcp's client transport. We don't need to implement any of this.

### 3. No auth on the proxy itself (v1)

For simplicity in v1, the proxy endpoint is unauthenticated — you're expected to
run it on a private network or behind a reverse proxy that handles auth. The web UI
is similarly open. A future version can add bearer token auth.

### 4. Error isolation

If one backend is down or errors, the proxy should:
- Still serve tools/resources from healthy backends
- Return partial results with a warning in list responses
- Return a clear JSON-RPC error if a `call` targets an unavailable backend

### 5. No tool rewriting beyond namespace prefix

Tools are passed through as-is (schema, description, etc.) with only the name
prefixed. The proxy doesn't transform inputs/outputs. This keeps it genuinely "dumb."

---

## MCP Proxy — Implementation with rmcp

### Axum Integration (in `main.rs`)

The `tower` feature lets us mount the MCP server as a tower service inside Axum:

```rust
use rmcp::transport::StreamableHttpServerTransport;

// Build the proxy handler (implements ServerHandler)
let proxy = ProxyHandler::new(pool.clone()).await;

// Create the streamable HTTP transport config
let mcp_config = StreamableHttpServerTransportConfig::default();

// Mount it in the Axum router alongside Leptos routes
let app = Router::new()
    .nest("/mcp", StreamableHttpServerTransport::axum_router(mcp_config, proxy))
    .leptos_routes(&app_state, routes, { /* shell */ })
    .fallback(leptos_axum::file_and_error_handler(shell))
    .with_state(app_state);
```

### ProxyHandler (implements `ServerHandler`)

This is the core of the proxy — an `rmcp::ServerHandler` implementation that
aggregates upstream servers:

```rust
use rmcp::{ServerHandler, ErrorData as McpError, RoleServer, model::*, service::RequestContext};
use std::sync::Arc;
use dashmap::DashMap;

/// Holds active rmcp client connections to upstream MCP servers.
struct ProxyHandler {
    pool: SqlitePool,
    /// slug → active rmcp client session
    upstreams: Arc<DashMap<String, RunningClientService>>,
}

impl ServerHandler for ProxyHandler {
    fn get_info(&self) -> ServerInfo {
        ServerInfo {
            instructions: Some("MCP Proxy — aggregates multiple upstream MCP servers".into()),
            capabilities: ServerCapabilities::builder()
                .enable_tools()
                .enable_resources()
                .enable_prompts()
                .build(),
            ..Default::default()
        }
    }

    /// Fan-out to all upstreams, prefix tool names with slug
    async fn list_tools(
        &self,
        _request: Option<PaginatedRequestParams>,
        _context: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, McpError> {
        let mut all_tools = Vec::new();

        for entry in self.upstreams.iter() {
            let slug = entry.key();
            let client = entry.value();

            match client.list_all_tools().await {
                Ok(tools) => {
                    for mut tool in tools {
                        tool.name = format!("{slug}__{}", tool.name).into();
                        all_tools.push(tool);
                    }
                }
                Err(e) => {
                    tracing::warn!(server = %slug, error = ?e, "Failed to list tools");
                }
            }
        }

        Ok(ListToolsResult { tools: all_tools, next_cursor: None })
    }

    /// Route to correct upstream by stripping namespace prefix
    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<CallToolResult, McpError> {
        let (slug, real_name) = request.name.split_once("__")
            .ok_or_else(|| McpError::invalid_params("Tool name missing namespace prefix", None))?;

        let client = self.upstreams.get(slug)
            .ok_or_else(|| McpError::invalid_params(
                &format!("No upstream server '{slug}'"), None
            ))?;

        // Forward with the original (un-prefixed) tool name
        client.call_tool(CallToolRequestParams {
            name: real_name.into(),
            arguments: request.arguments,
            meta: request.meta,
        }).await.map_err(|e| McpError::internal_error(
            &format!("Upstream '{slug}' error: {e}"), None
        ))
    }

    // list_resources, read_resource, list_prompts, get_prompt follow the same pattern...
}
```

### Upstream Connection Manager (`upstream.rs`)

```rust
use rmcp::{ServiceExt, transport::StreamableHttpClientTransport};

/// Connect to an upstream MCP server, returning a live client session.
pub async fn connect(url: &str, auth_header: Option<&str>) -> Result<RunningClientService> {
    let mut transport_builder = StreamableHttpClientTransport::builder(url.parse()?);

    if let Some(auth) = auth_header {
        transport_builder = transport_builder
            .with_header("Authorization", auth.parse()?);
    }

    let transport = transport_builder.build()?;

    // () as the client handler — we don't need to handle server→client requests
    let client = ().serve(transport).await?;

    Ok(client)
}

/// Refresh connections based on current DB state.
/// Called on startup and when config changes via the web UI.
pub async fn sync_connections(
    pool: &SqlitePool,
    upstreams: &DashMap<String, RunningClientService>,
) {
    let servers = db::get_enabled_servers(pool).await.unwrap_or_default();
    let desired_slugs: HashSet<_> = servers.iter().map(|s| s.slug.clone()).collect();

    // Remove connections for disabled/deleted servers
    upstreams.retain(|slug, _| desired_slugs.contains(slug));

    // Add connections for new/re-enabled servers
    for server in &servers {
        if !upstreams.contains_key(&server.slug) {
            match connect(&server.url, server.auth_header.as_deref()).await {
                Ok(client) => { upstreams.insert(server.slug.clone(), client); }
                Err(e) => tracing::error!(slug = %server.slug, error = ?e, "Failed to connect"),
            }
        }
    }
}
```

---

## Web UI Pages

### Dashboard (`/`)

- Grid/list of all configured servers
- Each card shows: name, slug, URL (truncated), status badge (●), tool count
- "Add Server" button → opens form
- Quick actions: enable/disable toggle, delete

### Server Detail (`/servers/:slug`)

- Full config form (edit name, slug, URL, auth header)
- "Test Connection" button — attempts `initialize` + `tools/list` and shows results
- Discovered capabilities listed in tabs: Tools | Resources | Prompts
- Each capability shows name, description, schema (collapsible JSON)
- "Refresh" button to re-fetch capabilities from upstream

### Settings (`/settings`)

- Proxy endpoint display (readonly, for copy-paste into AI client config)
- Proxy port configuration
- Future: proxy auth token, logging level, etc.

---

## Dockerfile (Multi-stage, same pattern as Hearr)

```dockerfile
# Stage 1: Build with cargo-leptos
FROM rust:1.87-bookworm AS builder

RUN wget https://github.com/cargo-bins/cargo-binstall/releases/latest/download/cargo-binstall-x86_64-unknown-linux-musl.tgz \
    && tar -xvf cargo-binstall-x86_64-unknown-linux-musl.tgz \
    && cp cargo-binstall /usr/local/cargo/bin \
    && rm -rf cargo-binstall*

RUN apt-get update && apt-get install -y --no-install-recommends clang && rm -rf /var/lib/apt/lists/*
RUN cargo binstall cargo-leptos -y
RUN rustup target add wasm32-unknown-unknown

WORKDIR /app
COPY Cargo.toml Cargo.lock ./
RUN mkdir src && echo "fn main() {}" > src/main.rs && echo "" > src/lib.rs
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/app/target \
    cargo fetch

COPY . .
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/app/target \
    cargo leptos build --release -vv \
    && cp target/release/dumb-mcp-server-proxy /app/app-bin \
    && cp -r target/site /app/site-out

# Stage 2: Minimal runtime
FROM debian:bookworm-slim AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends openssl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=builder /app/app-bin /app/server
COPY --from=builder /app/site-out /app/site
COPY --from=builder /app/Cargo.toml /app/

RUN mkdir -p /data

ENV RUST_LOG="info"
ENV LEPTOS_SITE_ADDR="0.0.0.0:3000"
ENV LEPTOS_SITE_ROOT="site"
ENV DATABASE_URL="sqlite:/data/proxy.db?mode=rwc"
EXPOSE 3000

CMD ["/app/server"]
```

---

## compose.yml

```yaml
services:
  proxy:
    build:
      context: .
      target: runtime
    container_name: dumb-mcp-proxy
    restart: unless-stopped
    ports:
      - '3000:3000'
    volumes:
      - proxy_data:/data
    environment:
      - RUST_LOG=info
      - LEPTOS_SITE_ADDR=0.0.0.0:3000
      - DATABASE_URL=sqlite:/data/proxy.db?mode=rwc

volumes:
  proxy_data:
```

---

## Implementation Phases

### Phase 1: Skeleton (get it running) ✅

- [x] `cargo leptos new` equivalent setup (Cargo.toml with leptos metadata, features)
- [x] Basic Axum server with Leptos shell rendering
- [x] SQLite schema + migrations via sqlx
- [x] Dockerfile + compose.yml
- [x] Tailwind setup

### Phase 2: Web UI (configure servers) ✅

- [x] Dashboard page with server list
- [x] Add/edit/delete server form (modal with validation)
- [x] Enable/disable toggle
- [x] Server detail page (`/servers/:slug`) with config display

### Phase 3: MCP Proxy Core (via rmcp) ✅

- [x] Add `rmcp` dependency with server + client + tower features
- [x] Implement `ServerHandler` trait on `ProxyHandler` struct
- [x] Mount `StreamableHttpService` at `/mcp` as a tower service
- [x] Upstream connection manager (`upstream.rs`) — connect/disconnect/reconnect
- [x] `list_tools` aggregation with namespace prefixing
- [x] `call_tool` routing by namespace
- [x] `list_resources`, `read_resource` (same pattern)
- [x] `list_prompts`, `get_prompt` (same pattern)
- [x] Wire upstream `sync()` on startup
- [x] Connection test validation on server creation (rejects bad URL/auth)
- [x] "Test Connection" button in add/edit form

### Phase 4: Polish & Observability

- [x] "Test Connection" button in UI (moved to Phase 3)
- [ ] Capability discovery + caching (server_capabilities table)
- [ ] Status badges (last successful contact, error state)
- [ ] Error isolation (partial results when a backend is down) — already implemented in proxy_handler
- [ ] Structured logging with tracing — already using tracing throughout

### Phase 5 (Future): Stdio MCP Servers

- [ ] Enable `rmcp` feature `transport-child-process`
- [ ] Extend `upstream.rs` to connect via `TokioChildProcess` transport
- [ ] Process lifecycle (start on enable, stop on disable, restart on crash)
- [ ] UI fields for command/args/env configuration
- [ ] No new abstraction needed — rmcp's `RunningService` is the same type
      regardless of whether the transport is HTTP or child process

---

## Open Questions / Decisions to Make Later

1. **Auth header storage**: Store plain text in SQLite for now? Or encrypt at rest
   with a master key from an env var? (Leaning toward plain text for v1 — it's a
   self-hosted tool on a private network.)

2. **Capability caching TTL**: How aggressively to cache `tools/list` results?
   Options: never cache (always fan out), cache with manual refresh button, cache
   with configurable TTL.

3. **Streaming responses**: rmcp's `transport-streamable-http-server` handles SSE
   streaming natively. The question is whether `call_tool` responses from upstream
   are streamed through or buffered. rmcp's client SDK returns full responses, so
   v1 naturally buffers. Streaming passthrough would require lower-level transport
   plumbing — defer.

4. **Multiple proxy endpoints**: Should there be one `/mcp` endpoint that serves
   everything, or the ability to create "profiles" that expose subsets of servers?
   (Leaning toward one endpoint for now — it's the "dumb" proxy after all.)

5. **Notifications / server-initiated messages**: MCP supports server→client
   notifications (e.g. `notifications/tools/list_changed`). The proxy could
   subscribe to these from backends and relay them. Defer to a later phase.

---

## References

- [MCP Specification](https://modelcontextprotocol.io/specification/2025-11-25)
- [rmcp — Official Rust MCP SDK](https://github.com/modelcontextprotocol/rust-sdk) (v1.7)
- [rmcp docs.rs](https://docs.rs/rmcp/latest/rmcp)
- [Leptos Book](https://book.leptos.dev/)
- [cargo-leptos](https://github.com/leptos-rs/cargo-leptos)
- Hearr (sibling project) — reference for Leptos + Axum + SQLite + Docker patterns
