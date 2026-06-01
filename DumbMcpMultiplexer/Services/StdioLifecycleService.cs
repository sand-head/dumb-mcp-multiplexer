using System.Collections.Concurrent;
using Docker.DotNet;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore;

namespace DumbMcpMultiplexer.Services;

public enum LifecycleState
{
    Running,
    Crashed,
    Restarting,
    Stopped
}

public sealed record LifecycleStatus(
    LifecycleState State,
    int RetryCount,
    string? LastError,
    DateTime? NextRetryAtUtc);

public sealed class StdioLifecycleService(
    IServiceScopeFactory scopeFactory,
    UpstreamManager upstreamManager,
    ILogger<StdioLifecycleService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableConnectionResetWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RestartBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(32),
        TimeSpan.FromSeconds(60)
    ];

    private readonly ConcurrentDictionary<string, LifecycleStatus> _states = new();
    private readonly ConcurrentDictionary<string, LifecycleTracker> _trackers = new();

    public IReadOnlyDictionary<string, LifecycleStatus> States => _states;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Stdio lifecycle service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunLifecycleCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in stdio lifecycle cycle");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunLifecycleCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var servers = await db.Servers
            .Where(s => s.Transport == "stdio_package_runner" || s.Transport == "stdio_container")
            .ToListAsync(ct);

        var activeSlugs = servers.Select(s => s.Slug).ToHashSet();
        foreach (var slug in _states.Keys.ToList())
        {
            if (!activeSlugs.Contains(slug))
            {
                _states.TryRemove(slug, out _);
                _trackers.TryRemove(slug, out _);
            }
        }

        foreach (var server in servers)
        {
            if (!server.Enabled)
            {
                SetStopped(server.Slug);
                continue;
            }

            await MonitorEnabledServerAsync(server, ct);
        }
    }

    private async Task MonitorEnabledServerAsync(McpServer server, CancellationToken ct)
    {
        if (!upstreamManager.TryGetContainerRuntime(server.Slug, out var runtime) || runtime is null)
        {
            await HandleCrashAsync(server, "No active stdio connection.", ct);
            return;
        }

        try
        {
            var inspect = await runtime.DockerClient.Containers.InspectContainerAsync(runtime.ContainerId, ct);
            if (inspect.State?.Running == true)
            {
                MarkRunning(server.Slug, runtime.ConnectedAtUtc);
                return;
            }

            var exitCode = inspect.State?.ExitCode;
            var details = string.IsNullOrWhiteSpace(inspect.State?.Error)
                ? inspect.State?.Status
                : inspect.State?.Error;
            await HandleCrashAsync(
                server,
                $"Container exited unexpectedly (code: {exitCode?.ToString() ?? "unknown"}{(string.IsNullOrWhiteSpace(details) ? string.Empty : $", {details}")}).",
                ct);
        }
        catch (DockerContainerNotFoundException)
        {
            await HandleCrashAsync(server, "Container disappeared before inspection.", ct);
        }
        catch (Exception ex)
        {
            await HandleCrashAsync(server, $"Container inspection failed: {ex.Message}", ct);
        }
    }

    private void MarkRunning(string slug, DateTime connectedAtUtc)
    {
        var tracker = _trackers.GetOrAdd(slug, _ => new LifecycleTracker());
        tracker.State = LifecycleState.Running;
        tracker.LastError = null;
        tracker.NextRetryAtUtc = null;
        tracker.ConnectedAtUtc = connectedAtUtc;

        if (tracker.RetryCount > 0
            && DateTime.UtcNow - tracker.ConnectedAtUtc > StableConnectionResetWindow)
        {
            tracker.RetryCount = 0;
        }

        Publish(slug, tracker);
    }

    private void SetStopped(string slug)
    {
        var tracker = _trackers.GetOrAdd(slug, _ => new LifecycleTracker());
        tracker.State = LifecycleState.Stopped;
        tracker.RetryCount = 0;
        tracker.LastError = null;
        tracker.NextRetryAtUtc = null;
        tracker.ConnectedAtUtc = DateTime.MinValue;
        Publish(slug, tracker);
    }

    private async Task HandleCrashAsync(McpServer server, string reason, CancellationToken ct)
    {
        var tracker = _trackers.GetOrAdd(server.Slug, _ => new LifecycleTracker());
        tracker.State = LifecycleState.Crashed;
        tracker.LastError = reason;
        tracker.NextRetryAtUtc ??= DateTime.UtcNow + GetBackoffDelay(tracker.RetryCount);
        Publish(server.Slug, tracker);

        if (DateTime.UtcNow < tracker.NextRetryAtUtc.Value)
        {
            return;
        }

        tracker.State = LifecycleState.Restarting;
        Publish(server.Slug, tracker);

        var reconnected = await upstreamManager.ReconnectAsync(server.Slug, ct);
        tracker.RetryCount++;

        if (reconnected)
        {
            tracker.State = LifecycleState.Running;
            tracker.LastError = null;
            tracker.NextRetryAtUtc = null;
            tracker.ConnectedAtUtc = DateTime.UtcNow;
            Publish(server.Slug, tracker);
            return;
        }

        tracker.State = LifecycleState.Crashed;
        tracker.LastError = $"Restart failed: {reason}";
        tracker.NextRetryAtUtc = DateTime.UtcNow + GetBackoffDelay(tracker.RetryCount);
        Publish(server.Slug, tracker);
    }

    private static TimeSpan GetBackoffDelay(int retryCount) =>
        RestartBackoff[Math.Min(retryCount, RestartBackoff.Length - 1)];

    private void Publish(string slug, LifecycleTracker tracker) =>
        _states[slug] = new LifecycleStatus(tracker.State, tracker.RetryCount, tracker.LastError, tracker.NextRetryAtUtc);

    private sealed class LifecycleTracker
    {
        public LifecycleState State { get; set; } = LifecycleState.Stopped;
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
        public DateTime? NextRetryAtUtc { get; set; }
        public DateTime ConnectedAtUtc { get; set; } = DateTime.MinValue;
    }
}
