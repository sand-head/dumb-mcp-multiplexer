# Stdio MCP Server Support — Design Plan

## Overview

Add support for local/stdio MCP servers to the multiplexer, using containers as the universal execution backend. Three modes of connecting to an upstream MCP server:

1. **Remote** — existing HTTP transport (already implemented)
2. **Package Runner** — specify a runner (`uvx`, `npx`) + package name; uses a well-known public base image with a shared cache volume, no build step
3. **Custom Containerfile** — provide a Containerfile; built via the Docker API, cached by content hash

All container communication happens through the Docker-compatible REST API socket (works identically with Podman and Docker).

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   DumbMcpMultiplexer                     │
│                                                         │
│  UpstreamManager                                        │
│  ├── Remote HTTP connections (existing)                 │
│  └── Stdio connections                                  │
│       ├── ContainerService (Docker.DotNet via socket)   │
│       ├── ImageResolver (package runner vs custom)      │
│       └── ContainerStdioTransport (attach stream)       │
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
| `ICSharpCode.SharpZipLib` | Tar archive creation for Containerfile build context (mode 3 only) |

## Mode 2: Package Runner

The simplest and most common local mode. Covers `uvx`, `npx`, and similar tools that fetch-and-run packages with built-in caching.

### How it works

No image build. Use a well-known public base image directly, pass the package command at runtime, and mount a persistent cache volume so packages are only downloaded once.

| Runner | Base Image | Cache Volume Path | Example Command |
|--------|-----------|-------------------|-----------------|
| `uvx` | `ghcr.io/astral-sh/uv:python3.12-slim` | `/root/.cache/uv` | `uvx music-assistant-mcp` |
| `npx` | `node:22-slim` | `/root/.npm/_npx` | `npx @modelcontextprotocol/server-filesystem /data` |

### Container creation

```csharp
var createParams = new CreateContainerParameters
{
    Image = GetBaseImageForRunner(server.PackageRunner), // "ghcr.io/astral-sh/uv:python3.12-slim"
    Cmd = ParseCommand(server.Command, server.Args),     // ["uvx", "music-assistant-mcp"]
    Env = ParseEnvVars(server.Env),
    OpenStdin = true,
    AttachStdin = true,
    AttachStdout = true,
    AttachStderr = true,
    Tty = false,
    HostConfig = new HostConfig
    {
        AutoRemove = true,
        Binds = BuildBinds(server),  // includes cache volume + user mounts
    }
};
```

### Cache volume

A single named volume (`dumb-mcp-runner-cache`) is shared across ALL package runner servers of the same runner type. This means:
- First start of any `uvx` MCP: downloads the package (~5-10s)
- Every subsequent start (same or different `uvx` MCP): near-instant from shared cache

### User-facing config

| Field | Example |
|-------|---------|
| Runner | `uvx` (dropdown) |
| Package / Command | `uvx music-assistant-mcp` |
| Environment Variables | `MA_SERVER_URL=http://10.0.0.5:8095`, `MA_TOKEN=abc123` |
| Volume Mounts | (optional, for MCPs needing host path access) |

## Mode 3: Custom Containerfile

For cases where package runners aren't enough — system dependencies, multiple packages, custom base images, or users who already have a pre-built image.

### How it works

User provides Containerfile content (or just an image name as a shortcut). The multiplexer builds it via the Docker API, caches by content hash, and runs it.

### "I already have an image" shortcut

If the user provides only an image reference (e.g. `ghcr.io/org/mcp-server:latest`), the Containerfile is implicitly:

```dockerfile
FROM ghcr.io/org/mcp-server:latest
```

No build needed — just pull and run. This covers the old "Tier 1 pre-built image" case.

### Full Containerfile

User pastes a complete Containerfile:

```dockerfile
FROM python:3.12-slim
RUN apt-get update && apt-get install -y git
RUN pip install --no-cache-dir some-complex-mcp
ENTRYPOINT ["python", "-m", "some_complex_mcp"]
```

### Image caching

- **Tag format:** `dumb-mcp-local/<slug>:<content-hash>`
- **Hash input:** SHA-256 of the Containerfile content (lowercase hex, first 12 chars)
- **Cache check:** `ListImagesAsync` filtering by tag — skip build if exists
- **Build:** pack Containerfile into in-memory tar, call `BuildImageFromDockerfileAsync`
- **Rebuild:** "Rebuild Image" button in UI forces a fresh build regardless of cache

### User-facing config

| Field | Example |
|-------|---------|
| Image (shortcut) | `ghcr.io/org/mcp-server:latest` |
| — OR — Containerfile | (textarea with full Containerfile content) |
| Command (optional override) | (overrides ENTRYPOINT if set) |
| Environment Variables | `KEY=value` |
| Volume Mounts | `/host/path:/container/path:ro` |

## Data Model

### `McpServer` Transport Types

| Value | Mode | Description |
|-------|------|-------------|
| `remote_http` | 1 | Existing remote HTTP/SSE transport |
| `stdio_package_runner` | 2 | Package runner (uvx/npx) with shared cache |
| `stdio_container` | 3 | Custom Containerfile or pre-built image |

### Fields on `McpServer`

| Field | Type | Used By | Description |
|-------|------|---------|-------------|
| `Transport` | string | All | Transport type discriminator |
| `Url` | string? | `remote_http` | Remote server URL |
| `Headers` | string | `remote_http` | JSON object of HTTP headers |
| `Command` | string? | Modes 2 & 3 | Command to execute (entrypoint) |
| `Args` | string? | Modes 2 & 3 | JSON array of command arguments |
| `Env` | string? | Modes 2 & 3 | JSON object of environment variables |
| `PackageRunner` | string? | Mode 2 | Runner ID: `uvx`, `npx` |
| `ContainerImage` | string? | Mode 3 | Pre-built image reference (shortcut) |
| `Containerfile` | string? | Mode 3 | Full Containerfile content |
| `ContainerMounts` | string? | Modes 2 & 3 | JSON array of volume mount specs |

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

- **Start on enable:** When a server is enabled (or on app startup for already-enabled servers), start the container
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
        "stdio_package_runner" => await CreatePackageRunnerTransport(server, ct),
        "stdio_container" => await CreateContainerTransport(server, ct),
        _ => throw new InvalidOperationException($"Unknown transport: {server.Transport}")
    };

    var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
    // ... store in _connections
}

private async Task<ITransport> CreatePackageRunnerTransport(McpServer server, CancellationToken ct)
{
    var image = GetBaseImageForRunner(server.PackageRunner);
    var cacheMount = GetCacheMountForRunner(server.PackageRunner);

    var mounts = ParseMounts(server.ContainerMounts);
    mounts.Add(cacheMount); // shared cache volume

    return await CreateAndAttachContainer(image, server, mounts, ct);
}

private async Task<ITransport> CreateContainerTransport(McpServer server, CancellationToken ct)
{
    string image;
    if (server.ContainerImage is not null)
    {
        // Pre-built image shortcut
        image = server.ContainerImage;
    }
    else if (server.Containerfile is not null)
    {
        // Build from Containerfile
        image = await _imageBuilder.EnsureImageAsync(server, ct);
    }
    else
    {
        throw new InvalidOperationException($"Server {server.Slug}: no image or Containerfile configured.");
    }

    return await CreateAndAttachContainer(image, server, ParseMounts(server.ContainerMounts), ct);
}

private async Task<ITransport> CreateAndAttachContainer(
    string image, McpServer server, List<string> binds, CancellationToken ct)
{
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
            Binds = binds,
        }
    };

    var container = await _containerService.Client
        .Containers.CreateContainerAsync(createParams, ct);

    var stream = await _containerService.Client
        .Containers.AttachContainerAsync(container.ID, tty: false,
            new ContainerAttachParameters { Stdin = true, Stdout = true, Stream = true }, ct);

    await _containerService.Client
        .Containers.StartContainerAsync(container.ID, null, ct);

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

The `%t` systemd specifier resolves automatically for both rootless and rootful deployments:

| Mode | Unit type | `%t` | Resulting socket path |
|------|-----------|------|-----------------------|
| Rootless | User unit (`~/.config/containers/systemd/`) | `/run/user/1000` | `/run/user/1000/podman/podman.sock` |
| Rootful | System unit (`/etc/containers/systemd/`) | `/run` | `/run/podman/podman.sock` |

No changes to the quadlet file are needed between modes. However, you must ensure the Podman API socket service is enabled for the appropriate scope:

```bash
# Rootless
systemctl --user enable --now podman.socket

# Rootful
systemctl enable --now podman.socket
```

For rootful deployments, the `CONTAINER_SOCKET` env var can also be omitted entirely — the multiplexer's auto-detection already tries `/run/podman/podman.sock` as a fallback.

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

```
┌─────────────────────────────────────────────────────────┐
│ Server Type:                                            │
│   ○ Remote HTTP                                         │
│   ● Package Runner (uvx, npx)                          │
│   ○ Custom Container                                    │
│                                                         │
│ Runner:   [uvx          ▾]                              │
│ Command:  [uvx music-assistant-mcp                    ] │
│                                                         │
│ Environment Variables:                                  │
│   [MA_SERVER_URL]  [http://10.0.0.5:8095]        [+ -] │
│   [MA_TOKEN]       [abc123]                      [+ -] │
│                                                         │
│ Volume Mounts (optional):                               │
│   Host Path          Container Path    Mode             │
│   [/home/user/docs]  [/data]           [ro ▾] [×]      │
│                                           [+ Add]       │
│                                                         │
│ [Test Connection]                          [Save]       │
└─────────────────────────────────────────────────────────┘
```

For "Custom Container" mode:

```
┌─────────────────────────────────────────────────────────┐
│ Server Type:                                            │
│   ○ Remote HTTP                                         │
│   ○ Package Runner (uvx, npx)                          │
│   ● Custom Container                                    │
│                                                         │
│ ○ I have a pre-built image                              │
│   Image: [ghcr.io/org/mcp-server:latest             ]   │
│                                                         │
│ ● I'll provide a Containerfile                          │
│   ┌───────────────────────────────────────────────────┐ │
│   │ FROM python:3.12-slim                             │ │
│   │ RUN pip install --no-cache-dir my-mcp-server      │ │
│   │ ENTRYPOINT ["python", "-m", "my_mcp"]            │ │
│   └───────────────────────────────────────────────────┘ │
│                                                         │
│ Command (optional, overrides ENTRYPOINT):               │
│   [                                                   ] │
│                                                         │
│ Environment Variables:                                  │
│   [KEY]  [VALUE]                                 [+ -] │
│                                                         │
│ Volume Mounts (optional):                               │
│   Host Path          Container Path    Mode             │
│                                           [+ Add]       │
│                                                         │
│ [Test Connection]  [Rebuild Image]         [Save]       │
└─────────────────────────────────────────────────────────┘
```

### Server Detail Page

Show container status:
- Container state (running/stopped/restarting)
- Image tag (for mode 3: includes content hash)
- Uptime / restart count
- "Rebuild Image" button (mode 3 with Containerfile only)
- "View Logs" (last N lines of container stderr)

## Implementation Phases

### Phase A: Package Runner Mode (Mode 2) ← DO THIS FIRST

- [x] Add `PackageRunner` field to `McpServer` model + migration
- [x] Add `Containerfile` field to `McpServer` model + migration
- [x] Rename/update transport type: `stdio_container` → support both `stdio_package_runner` and `stdio_container`
- [x] Implement package runner path in `UpstreamManager` (base image lookup + cache volume mount)
- [x] Create shared named volume `dumb-mcp-runner-cache-uvx` / `dumb-mcp-runner-cache-npx` on first use
- [x] UI: "Package Runner" server type with runner dropdown + command + env + mounts
- [x] Test with `uvx music-assistant-mcp`

### Phase B: Custom Containerfile Mode (Mode 3)

- [x] `ImageBuilderService` — Containerfile → in-memory tar → `BuildImageFromDockerfileAsync`
- [x] Content-hash tagging and cache check via `ListImagesAsync`
- [ ] UI: "Custom Container" type with image field OR Containerfile textarea
- [x] "Rebuild Image" button
- [ ] Test with a custom Containerfile

### Phase C: Lifecycle Management

- [ ] `StdioLifecycleService` (BackgroundService)
- [ ] Crash detection and restart with exponential backoff
- [ ] Health monitoring via MCP ping
- [ ] Status reporting to UI (running/stopped/failed/restarting)
- [x] Graceful shutdown on app exit

## Open Questions

1. **Cache volume naming** — One volume per runner type (`dumb-mcp-cache-uvx`, `dumb-mcp-cache-npx`)? Or one shared volume with subdirectories?

2. **Image pull policy** — For mode 2, should we pull the base image on every start, or only if missing? Leaning toward: pull if missing, offer a "pull latest" button in UI.

3. **Network access** — Default to full network access for MCP containers? Or restrict with `--network=none` and require explicit opt-in? Leaning toward allow by default (MCPs commonly need outbound access).

4. **Resource limits** — Set `--memory` / `--cpus` on spawned containers? Probably yes with generous defaults (512MB, 1 CPU) and per-server overrides.

5. **Container naming** — Name containers `dumb-mcp-<slug>` for easy identification in `podman ps`?
