using System.Collections.Concurrent;
using System.Text.Json;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Manages active MCP client connections to upstream servers.
/// Registered as a singleton service.
/// </summary>
public sealed class UpstreamManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, McpClient> _connections = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpstreamManager> _logger;

    public UpstreamManager(IServiceScopeFactory scopeFactory, ILogger<UpstreamManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the active client connections (slug → client).
    /// </summary>
    public IReadOnlyDictionary<string, McpClient> Connections => _connections;

    /// <summary>
    /// Connect to a single upstream MCP server.
    /// </summary>
    public async Task ConnectAsync(string slug, string url, Dictionary<string, string> headers, CancellationToken ct = default)
    {
        _logger.LogInformation("Connecting to upstream MCP server: {Slug} at {Url}", slug, url);

        var httpClient = new HttpClient();
        foreach (var (key, value) in headers)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            Name = slug,
        };

        var transport = new HttpClientTransport(transportOptions, httpClient);

        try
        {
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            // Remove old connection if it exists
            if (_connections.TryRemove(slug, out var old))
            {
                await old.DisposeAsync();
            }

            _connections[slug] = client;
            _logger.LogInformation("Successfully connected to upstream: {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to upstream: {Slug}", slug);
            httpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Disconnect from an upstream server.
    /// </summary>
    public async Task DisconnectAsync(string slug)
    {
        if (_connections.TryRemove(slug, out var client))
        {
            await client.DisposeAsync();
            _logger.LogInformation("Disconnected from upstream: {Slug}", slug);
        }
    }

    /// <summary>
    /// Test a connection to an upstream server without storing it.
    /// </summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(string url, Dictionary<string, string> headers, CancellationToken ct = default)
    {
        using var httpClient = new HttpClient();
        foreach (var (key, value) in headers)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            Name = "connection-test",
        };

        await using var transport = new HttpClientTransport(transportOptions, httpClient);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        var tools = await client.ListToolsAsync(cancellationToken: ct);

        return new ConnectionTestResult
        {
            ServerName = client.ServerInfo?.Name ?? "Unknown",
            ServerVersion = client.ServerInfo?.Version ?? "Unknown",
            ToolCount = tools.Count,
            ToolNames = tools.Select(t => t.Name).ToList()
        };
    }

    /// <summary>
    /// Synchronize connections based on the current database state.
    /// Connects to newly enabled servers, disconnects removed/disabled ones.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var enabledServers = await db.Servers
            .Where(s => s.Enabled && s.Url != null)
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
            if (!_connections.ContainsKey(server.Slug) && server.Url is not null)
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(server.Headers)
                    ?? new Dictionary<string, string>();
                try
                {
                    await ConnectAsync(server.Slug, server.Url, headers, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to connect to upstream during sync: {Slug}", server.Slug);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (slug, client) in _connections)
        {
            try { await client.DisposeAsync(); }
            catch { /* best effort */ }
        }
        _connections.Clear();
    }
}

public class ConnectionTestResult
{
    public string ServerName { get; set; } = string.Empty;
    public string ServerVersion { get; set; } = string.Empty;
    public int ToolCount { get; set; }
    public List<string> ToolNames { get; set; } = [];
}
