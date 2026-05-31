namespace DumbMcpMultiplexer.Middleware;

/// <summary>
/// Allows all cross-origin requests to the /mcp endpoint so web-based MCP clients
/// (e.g. Claude.ai, Cursor) can reach it from the browser without extra configuration.
/// </summary>
public class McpCorsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Accept, Authorization, Mcp-Session-Id";

        if (context.Request.Method == HttpMethods.Options)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await next(context);
    }
}
