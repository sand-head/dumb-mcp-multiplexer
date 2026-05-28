using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Services;
using Microsoft.EntityFrameworkCore;

namespace DumbMcpMultiplexer.Middleware;

/// <summary>
/// Middleware that validates the Host header against the dynamic allowed-hosts list.
/// Only enforced for requests to /mcp — the web UI is always reachable so users
/// can configure allowed hosts before the MCP endpoint is usable.
/// </summary>
public class HostGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HostGuardMiddleware> _logger;

    public HostGuardMiddleware(RequestDelegate next, ILogger<HostGuardMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        // Only enforce host checking on the MCP endpoint
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await _next(context);
            return;
        }

        var host = context.Request.Host.Host.ToLowerInvariant();

        // Load allowed hosts from database
        var hostsSetting = await db.Settings
            .Where(s => s.Key == "allowed_hosts")
            .Select(s => s.Value)
            .FirstOrDefaultAsync() ?? string.Empty;

        var allowedHosts = hostsSetting
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => h.Trim().ToLowerInvariant())
            .ToList();

        // Always allow loopback
        bool isLoopback = host is "localhost" or "127.0.0.1" or "::1";

        if (isLoopback)
        {
            await _next(context);
            return;
        }

        // If the list is empty, only loopback is allowed (safe default)
        if (allowedHosts.Count == 0)
        {
            _logger.LogWarning("Rejected request with disallowed Host header: {Host}", host);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(
                "Forbidden: Host not allowed. Configure allowed hosts in Settings.");
            return;
        }

        // Check if the host is in the allowed list
        bool isAllowed = allowedHosts.Any(allowed =>
        {
            // Match with or without port
            var allowedHost = allowed.Contains(':')
                ? allowed[..allowed.LastIndexOf(':')]
                : allowed;
            return allowedHost == host || allowed == host;
        });

        if (isAllowed)
        {
            await _next(context);
        }
        else
        {
            _logger.LogWarning("Rejected request with disallowed Host header: {Host}", host);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(
                "Forbidden: Host not allowed. Configure allowed hosts in Settings.");
        }
    }
}
