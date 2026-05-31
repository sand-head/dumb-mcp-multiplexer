using DumbMcpMultiplexer.Data;
using Microsoft.EntityFrameworkCore;

namespace DumbMcpMultiplexer.Middleware;

/// <summary>
/// Enforces API key authentication on the /mcp endpoint when a key is configured.
/// Accepts the key via "Authorization: Bearer &lt;key&gt;" or "?api_key=&lt;key&gt;".
/// If no key is stored in settings, the endpoint is unauthenticated (open).
/// </summary>
public class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        var storedKey = await db.Settings
            .Where(s => s.Key == "api_key")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        // No key configured — endpoint is open
        if (string.IsNullOrWhiteSpace(storedKey))
        {
            await next(context);
            return;
        }

        // Extract key from Authorization header or query string
        string? providedKey = null;

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            providedKey = authHeader["Bearer ".Length..].Trim();
        }
        else if (context.Request.Query.TryGetValue("api_key", out var queryKey))
        {
            providedKey = queryKey.ToString();
        }

        if (string.IsNullOrWhiteSpace(providedKey) || !CryptographicEquals(providedKey, storedKey))
        {
            logger.LogWarning("Rejected unauthenticated request to /mcp from {RemoteIp}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            await context.Response.WriteAsync("Unauthorized: valid API key required.");
            return;
        }

        await next(context);
    }

    // Constant-time comparison to avoid timing attacks
    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var result = 0;
        for (var i = 0; i < a.Length; i++)
            result |= a[i] ^ b[i];
        return result == 0;
    }
}
