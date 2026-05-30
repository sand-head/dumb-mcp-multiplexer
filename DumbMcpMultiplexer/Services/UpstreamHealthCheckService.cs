using Microsoft.Extensions.Hosting;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Background service that periodically probes each upstream MCP connection and
/// reconnects any that have gone stale (e.g. long-lived SSE sessions dropped by a
/// load-balancer or idle-timeout after several minutes of inactivity).
/// </summary>
public sealed class UpstreamHealthCheckService(
    UpstreamManager upstream,
    ILogger<UpstreamHealthCheckService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    // Simple per-slug back-off: skip a reconnect attempt if one was already tried
    // within this window so we don't hammer a persistently-unreachable upstream.
    private static readonly TimeSpan BackoffDuration = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTime> _lastReconnectAttempt = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Upstream health check service started (interval: {Interval})",
            CheckInterval);

        // Give the rest of the app a moment to finish startup before the first check.
        await DelayAsync(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunHealthCheckCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during upstream health check cycle");
            }

            await DelayAsync(CheckInterval, stoppingToken);
        }
    }

    private async Task RunHealthCheckCycleAsync(CancellationToken ct)
    {
        var slugs = upstream.Connections.Keys.ToList();
        if (slugs.Count == 0)
        {
            return;
        }

        logger.LogDebug("Running upstream health check for {Count} connection(s)", slugs.Count);

        foreach (var slug in slugs)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var healthy = await upstream.ProbeAsync(slug, ct);
            if (healthy)
            {
                continue;
            }

            // Respect back-off so we don't loop-reconnect a persistently-down upstream.
            if (_lastReconnectAttempt.TryGetValue(slug, out var lastAttempt)
                && DateTime.UtcNow - lastAttempt < BackoffDuration)
            {
                logger.LogDebug(
                    "Upstream {Slug} is unhealthy but back-off window has not elapsed; skipping reconnect",
                    slug);
                continue;
            }

            logger.LogWarning("Upstream {Slug} failed health check — attempting reconnect", slug);
            _lastReconnectAttempt[slug] = DateTime.UtcNow;

            var reconnected = await upstream.ReconnectAsync(slug, ct);
            if (reconnected)
            {
                _lastReconnectAttempt.Remove(slug);
                logger.LogInformation("Upstream {Slug} successfully reconnected", slug);
            }
            else
            {
                logger.LogError("Failed to reconnect upstream {Slug}; will retry after back-off", slug);
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Swallow — the while-loop condition handles shutdown cleanly.
        }
    }
}
