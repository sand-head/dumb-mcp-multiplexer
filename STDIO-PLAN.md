# Stdio MCP Server Support — Design Plan

## Overview

Add support for local/stdio MCP servers to the multiplexer, using containers (Podman/Docker) as the universal execution backend. Users specify a command and package dependencies; the multiplexer opaquely handles image building, caching, lifecycle management, and stdio transport — no container knowledge required from the end user.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   DumbMcpMultiplexer                     │
│                                                         │
│  UpstreamManager                                        │
│  ├── Remote HTTP connections (existing)                 │
│  └── Stdio connections (new)                            │
│       ├── ContainerService (Docker.DotNet via socket)   │
│       ├── ImageBuilder (generates + caches images)      │
│       └── ProcessLifecycleManager (start/stop/restart)  │
└─────────────────────────────────────────────────────────┘
        │ container attach (stdin/stdout stream)
        ▼
┌─────────────────────────────────────────────────────────┐
│  Container created via Docker-compatible API socket      │
│  (Podman or Docker — same REST API, same socket mount)  │
└─────────────────────────────────────────────────────────┘
```

Key principle: **talk to the socket, not the CLI**. Both Podman and Docker expose a Docker-compatible REST API over a Unix socket. We use `Docker.DotNet` to create containers, attach to their stdin/stdout streams, build images, and manage lifecycle — all through one code path regardless of which runtime the user has.

No CLI parsing. No "is this podman or docker" detection logic. One socket, one API.

## Dependencies

| Package | Purpose |
|---------|--------|
| [`Docker.DotNet`](https://github.com/dotnet/Docker.DotNet) | Docker-compatible API client over Unix socket (works with Podman and Docker) |
| `ModelContextProtocol` (existing) | MCP SDK — provides `ITransport` interface to implement |

## Execution Tiers

### Tier 1: Pre-built Image

User provides an existing container image. No build step needed.

- **Input:** Image reference (e.g. `ghcr.io/org/mcp-server:latest`), optional args, env, mounts
- **Execution:** Create + attach container via API socket (stdin/stdout stream)
- **Use case:** MCP servers that publish official Docker images

### Tier 2: Auto-built Image (Primary UX)

User provides a runtime, package dependencies, and a command. The multiplexer generates a Containerfile, builds it, and caches the result.

- **Input:** Runtime (`node`, `python`, `uvx`), packages list, command, optional env/mounts
- **Execution:** Build image via API → create + attach container via API
- **Use case:** NPX-based MCPs, pip-installed MCPs, any ecosystem with a package manager

### Tier 3: Custom Containerfile (Future / Power Users)

User provides a full Containerfile. Deferred to a later phase.

## Supported Runtimes (Tier 2)

| Runtime ID | Base Image | Install Command | Notes |
|------------|-----------|-----------------|-------|
| `node` | `node:22-slim` | `npm install -g {packages}` | For NPX-based MCP servers |
| `python` | `python:3.12-slim` | `pip install --no-cache-dir {packages}` | For pip-installed MCPs |
| `uvx` | `ghcr.io/astral-sh/uv:python3.12-slim` | `uv pip install {packages}` | Faster Python installs, better caching |

### Containerfile Templates

**Node:**
```dockerfile
FROM node:22-slim
RUN npm install -g {packages}
ENTRYPOINT [{command_as_json_array}]
```

**Python:**
```dockerfile
FROM python:3.12-slim
RUN pip install --no-cache-dir {packages}
ENTRYPOINT [{command_as_json_array}]
```

**uvx:**
```dockerfile
FROM ghcr.io/astral-sh/uv:python3.12-slim
RUN uv pip install --system {packages}
ENTRYPOINT [{command_as_json_array}]
```

## Image Caching Strategy

- **Tag format:** `dumb-mcp-local/<slug>:<content-hash>`
- **Hash input:** SHA-256 of `(runtime + sorted packages + command + base image tag)`
- **Rebuild trigger:** Only when the hash changes (i.e., user modifies config)
- **Cache check:** On server enable/startup, check if tagged image exists → skip build if yes
- **Pruning:** Offer a "rebuild image" button in the UI; old images can be pruned manually or on config change

## Data Model Changes

### `McpServer` Transport Types

Extend the `Transport` field enum:

| Value | Meaning |
|-------|---------|
| `remote_http` | Existing remote HTTP/SSE transport |
| `stdio_container` | Container-based stdio (Tier 1 & 2) |
| `stdio_native` | Native process stdio (bare-metal fallback) |

### New/Updated Fields on `McpServer`

| Field | Type | Used By | Description |
|-------|------|---------|-------------|
| `Transport` | string | All | Transport type discriminator |
| `Url` | string? | `remote_http` | Remote server URL |
| `Headers` | string | `remote_http` | JSON object of HTTP headers |
| `Command` | string? | `stdio_*` | Command to execute (entrypoint) |
| `Args` | string? | `stdio_*` | JSON array of command arguments |
| `Env` | string? | `stdio_*` | JSON object of environment variables |
| `ContainerImage` | string? | `stdio_container` | Pre-built image reference (Tier 1) |
| `ContainerRuntime` | string? | `stdio_container` | Runtime ID for auto-build (Tier 2): `node`, `python`, `uvx` |
| `ContainerPackages` | string? | `stdio_container` | JSON array of packages to install |
| `ContainerMounts` | string? | `stdio_container` | JSON array of volume mount specs |

### Migration

```sql
ALTER TABLE servers ADD COLUMN container_image TEXT;
ALTER TABLE servers ADD COLUMN container_runtime TEXT;
ALTER TABLE servers ADD COLUMN container_packages TEXT DEFAULT '[]';
ALTER TABLE servers ADD COLUMN container_mounts TEXT DEFAULT '[]';
```

## Container Socket Connection

Both Podman and Docker expose the same Docker Engine REST API over a Unix socket:

| Runtime | Rootless Socket | Rootful Socket |
|---------|----------------|----------------|
| Podman | `/run/user/$UID/podman/podman.sock` | `/run/podman/podman.sock` |
| Docker | — | `/var/run/docker.sock` |

The multiplexer connects to whichever socket is mounted into the container (or available on the host for bare-metal deployments). Configuration is a single env var or appsettings field:

```
CONTAINER_SOCKET=/run/podman/podman.sock
```

On startup:

1. Resolve socket path from config (default: try `/var/run/docker.sock`, then `/run/podman/podman.sock`)
2. Connect via `Docker.DotNet`'s `DockerClient` using `UnixDomainSocketEndPoint`
3. Call `SystemApi.PingAsync()` to verify connectivity
4. If unreachable and stdio servers are configured → log warning, mark those servers as errored

```csharp
public class ContainerService : IAsyncDisposable
{
    private readonly DockerClient _client;

    public ContainerService(IConfiguration config)
    {
        var socketPath = config["ContainerSocket"]
            ?? ResolveDefaultSocket();

        _client = new DockerClientConfiguration(
            new Uri($"unix://{socketPath}"))
            .CreateClient();
    }

    public bool IsAvailable { get; }          // set after Ping
    public string SocketPath { get; }         // for diagnostics/UI

    // Container lifecycle via Docker.DotNet API:
    // - CreateContainerAsync
    // - StartContainerAsync
    // - AttachContainerAsync (returns stdin/stdout multiplexed stream)
    // - StopContainerAsync / RemoveContainerAsync
    // - BuildImageFromDockerfileAsync
    // - ListImagesAsync (for cache checks)
}
```

This works identically for Podman and Docker — the user just mounts whichever socket they have.

## Process Lifecycle Management

A `BackgroundService` (`StdioLifecycleService`) manages all stdio MCP server processes:

### States

```
Stopped → Starting → Running → Stopping → Stopped
                  ↘ Failed (crash) → Restarting → Starting
```

### Behavior

- **Start on enable:** When a server is enabled (or on app startup for already-enabled servers), start the container process
- **Stop on disable:** Graceful shutdown (stop container via API → SIGTERM, then SIGKILL after timeout)
- **Restart on crash:** Exponential backoff (1s, 2s, 4s, 8s, ... max 60s), reset on successful connection lasting > 30s
- **Health monitoring:** Periodic `ping` via MCP protocol; mark as unhealthy after N failures

### Graceful Shutdown

1. Send MCP `shutdown` notification if supported
2. Close stdin pipe (signals EOF to well-behaved servers)
3. Wait up to 5s for process exit
4. Stop container via API (sends SIGTERM, waits 10s, then SIGKILL)

## UpstreamManager Refactor

```csharp
public async Task ConnectAsync(McpServer server, CancellationToken ct = default)
{
    IMcpTransport transport = server.Transport switch
    {
        "remote_http" => CreateHttpTransport(server),
        "stdio_container" => await CreateContainerStdioTransport(server, ct),
        "stdio_native" => CreateNativeStdioTransport(server),
        _ => throw new InvalidOperationException($"Unknown transport: {server.Transport}")
    };

    var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
    // ... store in _connections
}

private async Task<ITransport> CreateContainerStdioTransport(McpServer server, CancellationToken ct)
{
    // Ensure image exists (build if Tier 2)
    var image = await _imageBuilder.EnsureImageAsync(server, ct);

    // Create container via Docker-compatible API
    var createParams = new CreateContainerParameters
    {
        Image = image,
        Cmd = ParseCommand(server.Command, server.Args),
        Env = ParseEnvVars(server.Env),
        OpenStdin = true,
        AttachStdin = true,
        AttachStdout = true,
        AttachStderr = true,
        Tty = false,
        HostConfig = new HostConfig
        {
            AutoRemove = true,
            Binds = ParseMounts(server.ContainerMounts),
        }
    };

    var container = await _containerService.Client
        .Containers.CreateContainerAsync(createParams, ct);

    // Attach to stdin/stdout stream
    var stream = await _containerService.Client
        .Containers.AttachContainerAsync(container.ID, tty: false,
            new ContainerAttachParameters { Stdin = true, Stdout = true, Stream = true }, ct);

    await _containerService.Client
        .Containers.StartContainerAsync(container.ID, null, ct);

    // Wrap the multiplexed stream as an MCP transport
    return new ContainerStdioTransport(stream, container.ID, server.Slug);
}
```

The `ContainerStdioTransport` wraps `Docker.DotNet`'s `MultiplexedStream` (which provides stdin/stdout access to the attached container) into the `ITransport` interface expected by the MCP SDK. This is a thin adapter — read from stdout, write to stdin, same as `StdioClientTransport` but over the Docker attach stream instead of a child process.

## Deployment Changes

### Compose (Docker)

```yaml
services:
  proxy:
    image: ghcr.io/sand-head/dumb-mcp-multiplexer:latest
    # ... existing config ...
    volumes:
      - multiplexer_data:/data:Z
      # Mount the container runtime socket:
      - /var/run/docker.sock:/var/run/docker.sock
    environment:
      - CONTAINER_SOCKET=/var/run/docker.sock
```

### Compose (Podman — rootless)

```yaml
services:
  proxy:
    image: ghcr.io/sand-head/dumb-mcp-multiplexer:latest
    # ... existing config ...
    volumes:
      - multiplexer_data:/data:Z
      # Mount Podman's rootless socket:
      - ${XDG_RUNTIME_DIR:-/run/user/1000}/podman/podman.sock:/run/podman/podman.sock
    environment:
      - CONTAINER_SOCKET=/run/podman/podman.sock
```

### Quadlet (Podman)

```ini
[Container]
# ... existing config ...
Volume=%t/podman/podman.sock:/run/podman/podman.sock
Environment=CONTAINER_SOCKET=/run/podman/podman.sock
```

### Bare-metal (no container deployment)

If the multiplexer is running directly on a host with Docker or Podman installed, it will auto-detect the socket at the default path. No additional configuration needed.

### Security Considerations

- **Socket mount grants container management privileges.** Document this clearly. The multiplexer can create/start/stop/remove containers on the host.
- **Podman rootless** mitigates the blast radius — spawned containers run as the unprivileged user, not root.
- **Docker socket = root access** on the host (unless using rootless Docker). Users should understand this tradeoff.
- **Optional:** Only mount the socket when stdio servers are needed. The multiplexer gracefully degrades to HTTP-only mode without it.
- **`read_only: true` and `cap_drop: ALL`** can remain on the multiplexer container itself — socket access doesn't require elevated caps.
- **Future:** Consider a restricted API proxy (like [Docker Socket Proxy](https://github.com/Tecnativa/docker-socket-proxy)) between the multiplexer and the raw socket to limit which API calls are allowed.

## UI Changes

### Add/Edit Server Form

When transport type is "Local Command":

```
┌─────────────────────────────────────────────────────┐
│ Server Type: ○ Remote HTTP  ● Local Command         │
│                                                     │
│ Execution Mode: ○ Pre-built Image  ● Auto-build    │
│                                                     │
│ Runtime:  [Node.js     ▾]                           │
│ Packages: [@modelcontextprotocol/server-filesystem] │
│ Command:  [npx @modelcontextprotocol/server-files…] │
│                                                     │
│ Environment Variables:                              │
│   [KEY]  [VALUE]                              [+ -] │
│                                                     │
│ Volume Mounts:                                      │
│   Host Path          Container Path    Mode         │
│   [/home/user/docs]  [/data]           [ro ▾] [×]  │
│                                           [+ Add]   │
│                                                     │
│ [Test Connection]                          [Save]   │
└─────────────────────────────────────────────────────┘
```

### Server Detail Page

Show container status:
- Image tag + hash
- Container state (running/stopped/restarting)
- Uptime / restart count
- "Rebuild Image" button
- "View Logs" (last N lines of container stderr)

## Implementation Phases

### Phase S1: Foundation

- [x] `ContainerRuntimeService` — detect podman/docker on startup
- [x] Data model migration (new columns)
- [x] Update `McpServer` model and EF mappings

### Phase S2: Tier 1 (Pre-built Image)

- [x] `StdioClientTransport` integration in `UpstreamManager`
- [x] Container create + attach via `Docker.DotNet` API
- [x] Basic lifecycle (start/stop)
- [x] UI: transport type selector, image field, env/mounts config
- [x] Test with a known MCP server image

### Phase S3: Tier 2 (Auto-build)

- [ ] `ImageBuilderService` — Containerfile template generation
- [ ] Content-hash tagging and cache check
- [ ] Image build via `Docker.DotNet` API (`BuildImageFromDockerfileAsync`)
- [ ] UI: runtime selector, packages field, command field
- [ ] Test with NPX and pip-based MCP servers

### Phase S4: Lifecycle Management

- [ ] `StdioLifecycleService` (BackgroundService)
- [ ] Crash detection and restart with exponential backoff
- [ ] Health monitoring via MCP ping
- [ ] Status reporting to UI (running/stopped/failed/restarting)
- [ ] Graceful shutdown on app exit

### Phase S5: Polish

- [ ] "Rebuild Image" action in UI
- [ ] Container log viewing (stderr capture)
- [ ] "Test Connection" for stdio servers (build + run + handshake + teardown)
- [ ] Documentation for socket mount setup
- [x] Docker Compose + Podman Quadlet updates with socket mount examples

## Open Questions

1. **Image garbage collection** — When a server's config changes and a new image is built, should we auto-remove the old one? Or leave it for manual pruning?

2. **Concurrent builds** — If multiple stdio servers are enabled simultaneously on first boot, should image builds run in parallel or be serialized? (Parallel is faster but may thrash disk I/O.)

3. **Registry pull policy** — For Tier 1 (pre-built images), should we always pull latest, or respect the tag? Likely: pull on first connect, then use cached. Offer a "pull latest" button.

4. **Network access from MCP containers** — Some MCP servers need outbound internet (e.g., fetch MCP, web search MCP). Default to allowing network access? Or default-deny with explicit opt-in? Leaning toward allowing by default since the user explicitly configured the server.

5. **Resource limits** — Should we set `--memory` / `--cpus` limits on spawned containers? Probably yes with generous defaults (e.g., 512MB RAM, 1 CPU) and per-server overrides.

6. **Multi-arch** — Auto-built images inherit the host arch. This is fine for single-host deployments but worth noting for anyone running ARM vs x86.
