using System.Collections.Concurrent;
using System.Text.Json;
using Docker.DotNet.Models;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Manages active MCP client connections to upstream servers.
/// Registered as a singleton service.
/// </summary>
public sealed class UpstreamManager(
    IServiceScopeFactory scopeFactory,
    ContainerService containerService,
    ImageBuilderService imageBuilder,
    IConfiguration configuration,
    ILogger<UpstreamManager> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, McpClient> _connections = new();
    private readonly ConcurrentDictionary<string, ActiveConnection> _activeConnections = new();
    private readonly ConcurrentDictionary<string, UpstreamHealthStatus> _healthStatuses = new();

    private static readonly Dictionary<string, (string Image, string CacheEnvVar, string CacheSubdir)> PackageRunners = new()
    {
        ["uvx"] = ("ghcr.io/astral-sh/uv:python3.12-trixie-slim", "UV_CACHE_DIR", "/mnt/cache/uv"),
        ["npx"] = ("node:22-slim", "NPM_CONFIG_CACHE", "/mnt/cache/npx"),
    };

    /// <summary>
    /// Gets the active client connections (slug → client).
    /// </summary>
    public IReadOnlyDictionary<string, McpClient> Connections => _connections;

    /// <summary>
    /// Gets the last known health status for each connected upstream.
    /// Absent means not yet probed (assume healthy).
    /// </summary>
    public IReadOnlyDictionary<string, UpstreamHealthStatus> HealthStatuses => _healthStatuses;

    /// <summary>
    /// Connect to a single upstream MCP server.
    /// </summary>
    public async Task ConnectAsync(McpServer server, CancellationToken ct = default)
    {
        logger.LogInformation("Connecting to upstream MCP server: {Slug} ({Transport})", server.Slug, server.Transport);

        var newConnection = await CreateConnectionAsync(server, ct);

        if (_activeConnections.TryRemove(server.Slug, out var old))
        {
            await old.DisposeAsync();
        }

        _activeConnections[server.Slug] = newConnection;
        _connections[server.Slug] = newConnection.Client;
        _healthStatuses[server.Slug] = UpstreamHealthStatus.Healthy;
        logger.LogInformation("Successfully connected to upstream: {Slug}", server.Slug);
    }

    /// <summary>
    /// Disconnect from an upstream server.
    /// </summary>
    public async Task DisconnectAsync(string slug)
    {
        _connections.TryRemove(slug, out _);
        _healthStatuses.TryRemove(slug, out _);
        if (_activeConnections.TryRemove(slug, out var connection))
        {
            await connection.DisposeAsync();
            logger.LogInformation("Disconnected from upstream: {Slug}", slug);
        }
    }

    /// <summary>
    /// Test a connection to an upstream server without storing it.
    /// </summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(McpServer server, CancellationToken ct = default)
    {
        var connection = await CreateConnectionAsync(server, ct);
        try
        {
            var tools = await connection.Client.ListToolsAsync(cancellationToken: ct);
            return new ConnectionTestResult
            {
                ServerName = connection.Client.ServerInfo?.Name ?? "Unknown",
                ServerVersion = connection.Client.ServerInfo?.Version ?? "Unknown",
                ToolCount = tools.Count,
                ToolNames = tools.Select(t => t.Name).ToList()
            };
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Probes a single upstream connection with a short timeout.
    /// Returns true if the connection is healthy, false if it failed or timed out.
    /// </summary>
    public async Task<bool> ProbeAsync(string slug, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(slug, out var client))
        {
            return false;
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await client.ListToolsAsync(cancellationToken: probeCts.Token);
            _healthStatuses[slug] = UpstreamHealthStatus.Healthy;
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Upstream probe timed out for {Slug}", slug);
            _healthStatuses[slug] = UpstreamHealthStatus.Degraded;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream probe failed for {Slug}", slug);
            _healthStatuses[slug] = UpstreamHealthStatus.Degraded;
            return false;
        }
    }

    /// <summary>
    /// Reconnects a single upstream by slug, looking up its configuration from the database.
    /// </summary>
    public async Task<bool> ReconnectAsync(string slug, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Slug == slug, ct);

        if (server is null || !server.Enabled)
        {
            logger.LogWarning("Cannot reconnect upstream {Slug}: server not found or not enabled", slug);
            return false;
        }

        try
        {
            await ConnectAsync(server, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reconnect to upstream: {Slug}", slug);
            return false;
        }
    }

    /// <summary>
    /// Synchronize connections based on the current database state.
    /// Connects to newly enabled servers, disconnects removed/disabled ones.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var enabledServers = await db.Servers
            .Where(s => s.Enabled &&
                ((s.Transport == "remote_http" && s.Url != null) ||
                 (s.Transport == "stdio_package_runner" && s.PackageRunner != null) ||
                 (s.Transport == "stdio_container" && (s.ContainerImage != null || s.Containerfile != null))))
            .ToListAsync(ct);

        var desiredSlugs = enabledServers.Select(s => s.Slug).ToHashSet();

        // Remove connections for disabled/deleted servers
        foreach (var slug in _connections.Keys.ToList())
        {
            if (!desiredSlugs.Contains(slug))
            {
                await DisconnectAsync(slug);
            }
        }

        // Add connections for new/re-enabled servers
        foreach (var server in enabledServers)
        {
            if (_connections.ContainsKey(server.Slug))
            {
                continue;
            }

            try
            {
                await ConnectAsync(server, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to connect to upstream during sync: {Slug}", server.Slug);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var slug in _connections.Keys.ToList())
        {
            try
            {
                await DisconnectAsync(slug);
            }
            catch
            {
                // best effort
            }
        }
    }

    private async Task<ActiveConnection> CreateConnectionAsync(McpServer server, CancellationToken ct)
    {
        return server.Transport switch
        {
            "remote_http" => await CreateRemoteHttpConnectionAsync(server, ct),
            "stdio_container" => await CreateContainerConnectionAsync(server, ct),
            "stdio_package_runner" => await CreatePackageRunnerConnectionAsync(server, ct),
            _ => throw new InvalidOperationException($"Unsupported transport: {server.Transport}")
        };
    }

    private static async Task<ActiveConnection> CreateRemoteHttpConnectionAsync(McpServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
        {
            throw new InvalidOperationException("Remote HTTP transport requires a URL.");
        }

        var headers = DeserializeStringDictionary(server.Headers);
        var httpClient = new HttpClient();
        foreach (var (key, value) in headers)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Url),
            Name = server.Slug,
        };

        try
        {
            var transport = new HttpClientTransport(transportOptions, httpClient);
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            return new ActiveConnection(client);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    private async Task<ActiveConnection> CreateContainerConnectionAsync(McpServer server, CancellationToken ct)
    {
        if (!containerService.IsAvailable || containerService.Client is null)
        {
            throw new InvalidOperationException("Container runtime socket is unavailable.");
        }

        // Resolve image: Tier 1 (pre-built) or Tier 2 (auto-build)
        var resolvedImage = await imageBuilder.EnsureImageAsync(server, ct);

        var docker = containerService.Client;

        var createParams = new CreateContainerParameters
        {
            Image = resolvedImage,
            Cmd = BuildCommand(server.Command, server.Args),
            Env = BuildEnvList(server.Env),
            OpenStdin = true,
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            HostConfig = new HostConfig
            {
                AutoRemove = true,
                Binds = BuildMounts(server.ContainerMounts)
            }
        };

        var created = await docker.Containers.CreateContainerAsync(createParams, ct);
        var containerId = created.ID;
        ContainerStdioTransport? transport = null;
        try
        {
            var attachStream = await docker.Containers.AttachContainerAsync(
                containerId,
                false,
                new ContainerAttachParameters
                {
                    Stdin = true,
                    Stdout = true,
                    Stderr = true,
                    Stream = true
                },
                ct);

            var started = await docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);
            if (!started)
            {
                throw new InvalidOperationException($"Failed to start container '{containerId}'.");
            }

            transport = new ContainerStdioTransport(attachStream, server.Slug);
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            return new ActiveConnection(client, transport, containerId, docker);
        }
        catch
        {
            if (transport is not null)
            {
                try { await transport.DisposeAsync(); } catch { /* best-effort */ }
            }

            await StopAndRemoveContainerAsync(docker, containerId);
            throw;
        }
    }

    private async Task<ActiveConnection> CreatePackageRunnerConnectionAsync(McpServer server, CancellationToken ct)
    {
        if (!containerService.IsAvailable || containerService.Client is null)
        {
            throw new InvalidOperationException("Container runtime socket is unavailable.");
        }

        if (string.IsNullOrWhiteSpace(server.PackageRunner))
        {
            throw new InvalidOperationException($"Server '{server.Slug}': PackageRunner is required for stdio_package_runner transport.");
        }

        if (!PackageRunners.TryGetValue(server.PackageRunner, out var runnerConfig))
        {
            throw new InvalidOperationException(
                $"Unsupported package runner: '{server.PackageRunner}'. Supported: {string.Join(", ", PackageRunners.Keys)}");
        }

        var cacheVolume = configuration["RunnerCacheVolume"] ?? configuration["RUNNER_CACHE_VOLUME"];
        if (string.IsNullOrWhiteSpace(cacheVolume))
        {
            throw new InvalidOperationException(
                "RUNNER_CACHE_VOLUME is not configured. Set this environment variable to a named volume (e.g. 'dumb-mcp-runner-cache') before using stdio package runner servers.");
        }

        var docker = containerService.Client;

        // Build mounts: user mounts + shared cache volume
        var binds = BuildMounts(server.ContainerMounts)?.ToList() ?? [];
        binds.Add($"{cacheVolume}:/mnt/cache");

        // Build env: user env + cache directory env var for this runner
        var env = BuildEnvList(server.Env)?.ToList() ?? [];
        env.Add($"{runnerConfig.CacheEnvVar}={runnerConfig.CacheSubdir}");

        var createParams = new CreateContainerParameters
        {
            Image = runnerConfig.Image,
            Cmd = BuildCommand(server.Command, server.Args),
            Env = env,
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

        var created = await docker.Containers.CreateContainerAsync(createParams, ct);
        var containerId = created.ID;
        ContainerStdioTransport? transport = null;
        try
        {
            var attachStream = await docker.Containers.AttachContainerAsync(
                containerId,
                false,
                new ContainerAttachParameters
                {
                    Stdin = true,
                    Stdout = true,
                    Stderr = true,
                    Stream = true
                },
                ct);

            var started = await docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);
            if (!started)
            {
                throw new InvalidOperationException($"Failed to start container '{containerId}'.");
            }

            transport = new ContainerStdioTransport(attachStream, server.Slug);
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            return new ActiveConnection(client, transport, containerId, docker);
        }
        catch
        {
            if (transport is not null)
            {
                try { await transport.DisposeAsync(); } catch { /* best-effort */ }
            }

            await StopAndRemoveContainerAsync(docker, containerId);
            throw;
        }
    }

    private static Dictionary<string, string> DeserializeStringDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>();
    }

    private static IList<string>? BuildCommand(string? command, string? argsJson)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(command))
        {
            args.AddRange(CommandTokenizer.Tokenize(command));
        }

        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(argsJson) ?? [];
            args.AddRange(parsed.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        return args.Count > 0 ? args : null;
    }

    private static IList<string>? BuildEnvList(string? envJson)
    {
        if (string.IsNullOrWhiteSpace(envJson))
        {
            return null;
        }

        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(envJson);
        if (env is null || env.Count == 0)
        {
            return null;
        }

        return env.Select(kvp => $"{kvp.Key}={kvp.Value}").ToList();
    }

    private static IList<string>? BuildMounts(string? mountsJson)
    {
        if (string.IsNullOrWhiteSpace(mountsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(mountsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var binds = new List<string>();
            foreach (var mount in doc.RootElement.EnumerateArray())
            {
                if (mount.ValueKind == JsonValueKind.String)
                {
                    var value = mount.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        binds.Add(value);
                    }
                    continue;
                }

                if (mount.ValueKind == JsonValueKind.Object
                    && mount.TryGetProperty("HostPath", out var hostPath)
                    && mount.TryGetProperty("ContainerPath", out var containerPath))
                {
                    var mode = mount.TryGetProperty("Mode", out var modeProperty)
                        ? (modeProperty.GetString() ?? "rw")
                        : "rw";

                    var host = hostPath.GetString();
                    var container = containerPath.GetString();
                    if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(container))
                    {
                        binds.Add($"{host}:{container}:{mode}");
                    }
                }
            }

            return binds.Count == 0 ? null : binds;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task StopAndRemoveContainerAsync(Docker.DotNet.DockerClient client, string containerId)
    {
        try
        {
            await client.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
        }
        catch
        {
            // Best effort
        }

        try
        {
            await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
        }
        catch
        {
            // Best effort
        }
    }

    private sealed class ActiveConnection(
        McpClient client,
        IAsyncDisposable? transport = null,
        string? containerId = null,
        Docker.DotNet.DockerClient? dockerClient = null) : IAsyncDisposable
    {
        public McpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync();
            }
            catch
            {
                // best effort
            }

            if (transport is not null)
            {
                try
                {
                    await transport.DisposeAsync();
                }
                catch
                {
                    // best effort
                }
            }

            if (!string.IsNullOrWhiteSpace(containerId) && dockerClient is not null)
            {
                await StopAndRemoveContainerAsync(dockerClient, containerId);
            }
        }
    }
}

public class ConnectionTestResult
{
    public string ServerName { get; set; } = string.Empty;
    public string ServerVersion { get; set; } = string.Empty;
    public int ToolCount { get; set; }
    public List<string> ToolNames { get; set; } = [];
}

public enum UpstreamHealthStatus
{
    /// <summary>Connection is active and the last probe succeeded (or the server just connected).</summary>
    Healthy,
    /// <summary>The last probe failed or timed out; a reconnect attempt may be in progress.</summary>
    Degraded,
}
