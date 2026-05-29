using DumbMcpMultiplexer.Components;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Middleware;
using DumbMcpMultiplexer.Models;
using DumbMcpMultiplexer.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=app.db"));

builder.Services.AddScoped<ServerService>();
builder.Services.AddSingleton<UpstreamManager>();
builder.Services.AddSingleton<DiscoveredToolsTracker>();

// Configure the MCP server with Streamable HTTP transport.
// We use custom handlers to dynamically aggregate tools/resources/prompts from upstream servers.
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation
    {
        Name = "dumb-mcp-multiplexer",
        Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.1"
    };
    options.Capabilities = new ServerCapabilities
    {
        Tools = new ToolsCapability { ListChanged = true },
        Resources = new ResourcesCapability(),
        Prompts = new PromptsCapability()
    };
})
.WithHttpTransport(options =>
{
#pragma warning disable MCPEXP002 // RunSessionHandler is experimental
    options.RunSessionHandler = async (httpContext, server, ct) =>
    {
        try
        {
            await server.RunAsync(ct);
        }
        finally
        {
            // Clean up discovered tools for this session when it ends
            var sessionId = server.SessionId;
            if (sessionId is not null)
            {
                var tracker = httpContext.RequestServices.GetRequiredService<DiscoveredToolsTracker>();
                tracker.ClearSession(sessionId);
            }
        }
    };
#pragma warning restore MCPEXP002
})
.WithListToolsHandler(async (request, ct) =>
{
    var upstream = request.Services!.GetRequiredService<UpstreamManager>();
    var db = request.Services!.GetRequiredService<AppDbContext>();
    var logger = request.Services!.GetRequiredService<ILogger<Program>>();

    // If progressive discovery is enabled, return meta-tools + any previously discovered tools for this session
    if (await ProgressiveDiscoveryService.IsEnabledAsync(db, ct))
    {
        var tracker = request.Services!.GetRequiredService<DiscoveredToolsTracker>();
        var sessionId = request.Server.SessionId ?? "";
        var tools2 = new List<Tool>(ProgressiveDiscoveryService.GetMetaTools());
        tools2.AddRange(tracker.GetDiscoveredTools(sessionId));
        return new ListToolsResult { Tools = tools2 };
    }

    var tools = new List<Tool>();
    var connectedSlugs = upstream.Connections.Keys.ToList();

    var disabledToolLookup = await db.ServerCapabilities
        .Where(c => c.Kind == ServerCapability.ToolKind && !c.Enabled && connectedSlugs.Contains(c.Server.Slug))
        .Select(c => new { c.Server.Slug, c.Name })
        .ToListAsync(ct);
    var disabledToolsBySlug = disabledToolLookup
        .GroupBy(x => x.Slug, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(x => x.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    foreach (var (slug, client) in upstream.Connections)
    {
        try
        {
            var upstreamTools = await client.ListToolsAsync(cancellationToken: ct);
            foreach (var tool in upstreamTools)
            {
                if (disabledToolsBySlug.TryGetValue(slug, out var disabledTools) && disabledTools.Contains(tool.Name))
                {
                    continue;
                }

                tools.Add(new Tool
                {
                    Name = Namespace.Prefix(slug, tool.Name),
                    Description = tool.Description,
                    InputSchema = tool.JsonSchema
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list tools from upstream: {Slug}", slug);
        }
    }

    return new ListToolsResult { Tools = tools };
})
.WithCallToolHandler(async (request, ct) =>
{
    var upstream = request.Services!.GetRequiredService<UpstreamManager>();
    var db = request.Services!.GetRequiredService<AppDbContext>();
    var logger = request.Services!.GetRequiredService<ILogger<Program>>();
    var toolName = request.Params?.Name ?? "";

    // Handle progressive discovery meta-tools
    if (toolName == ProgressiveDiscoveryService.SearchToolName)
    {
        var query = request.Params?.Arguments?.TryGetValue("query", out var q) == true ? q.ToString() : "";
        var serverFilter2 = request.Params?.Arguments?.TryGetValue("server", out var s) == true ? s.ToString() : null;

        // Get matching tools once (used for both the response and tracker registration)
        var matchingTools = await ProgressiveDiscoveryService.GetMatchingToolsAsync(upstream, db, query, serverFilter2, logger, ct);
        var result = ProgressiveDiscoveryService.FormatSearchResult(matchingTools);

        // Register discovered tools so they appear in subsequent tools/list responses
        if (await ProgressiveDiscoveryService.IsEnabledAsync(db, ct))
        {
            var tracker = request.Services!.GetRequiredService<DiscoveredToolsTracker>();
            var sessionId = request.Server.SessionId ?? "";
            if (tracker.RegisterDiscoveredTools(sessionId, matchingTools))
            {
                // Notify the client that the tool list has changed so it re-fetches tools/list
                await request.Server.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, ct);
            }
        }

        return result;
    }

    if (toolName == ProgressiveDiscoveryService.ListCategoriesToolName)
    {
        return await ProgressiveDiscoveryService.HandleListCategoriesAsync(upstream, logger, ct);
    }

    var split = Namespace.Split(toolName);
    if (split is null)
    {
        throw new McpProtocolException(
            $"Tool name '{request.Params?.Name}' is missing namespace prefix (expected format: slug__tool_name)",
            McpErrorCode.InvalidParams);
    }

    var (slug, realName) = split.Value;

    var toolDisabled = await db.ServerCapabilities
        .AnyAsync(c => c.Kind == ServerCapability.ToolKind && !c.Enabled && c.Name == realName && c.Server.Slug == slug, ct);
    if (toolDisabled)
    {
        throw new McpProtocolException(
            $"Tool '{request.Params?.Name}' is disabled by multiplexer configuration",
            McpErrorCode.InvalidParams);
    }

    if (!upstream.Connections.TryGetValue(slug, out var client))
    {
        throw new McpProtocolException(
            $"No upstream server with slug '{slug}'",
            McpErrorCode.InvalidParams);
    }

    var arguments = request.Params?.Arguments?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
    return await client.CallToolAsync(realName, arguments, cancellationToken: ct);
})
.WithListResourcesHandler(async (request, ct) =>
{
    var upstream = request.Services!.GetRequiredService<UpstreamManager>();
    var logger = request.Services!.GetRequiredService<ILogger<Program>>();
    var resources = new List<Resource>();

    foreach (var (slug, client) in upstream.Connections)
    {
        try
        {
            var upstreamResources = await client.ListResourcesAsync(cancellationToken: ct);
            foreach (var resource in upstreamResources)
            {
                resources.Add(new Resource
                {
                    Uri = Namespace.PrefixUri(slug, resource.Uri),
                    Name = Namespace.Prefix(slug, resource.Name),
                    Description = resource.Description,
                    MimeType = resource.MimeType
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list resources from upstream: {Slug}", slug);
        }
    }

    return new ListResourcesResult { Resources = resources };
})
.WithReadResourceHandler(async (request, ct) =>
{
    var upstream = request.Services!.GetRequiredService<UpstreamManager>();

    var split = Namespace.SplitUri(request.Params?.Uri ?? "");
    if (split is null)
    {
        throw new McpProtocolException(
            $"Resource URI '{request.Params?.Uri}' is missing namespace prefix (expected format: proxy://slug/uri)",
            McpErrorCode.InvalidParams);
    }

    var (slug, realUri) = split.Value;
    if (!upstream.Connections.TryGetValue(slug, out var client))
    {
        throw new McpProtocolException(
            $"No upstream server with slug '{slug}'",
            McpErrorCode.InvalidParams);
    }

    return await client.ReadResourceAsync(realUri, cancellationToken: ct);
})
.WithListPromptsHandler(async (request, ct) =>
{
    var upstream = request.Services!.GetRequiredService<UpstreamManager>();
    var logger = request.Services!.GetRequiredService<ILogger<Program>>();
    var prompts = new List<Prompt>();

    foreach (var (slug, client) in upstream.Connections)
    {
        try
        {
            var upstreamPrompts = await client.ListPromptsAsync(cancellationToken: ct);
            foreach (var prompt in upstreamPrompts)
            {
                prompts.Add(new Prompt
                {
                    Name = Namespace.Prefix(slug, prompt.Name),
                    Description = prompt.Description,
                    Arguments = prompt.ProtocolPrompt.Arguments
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list prompts from upstream: {Slug}", slug);
        }
    }

    return new ListPromptsResult { Prompts = prompts };
})
.WithGetPromptHandler(async (request, ct) =>
{
    var upstream = request.Services!.GetRequiredService<UpstreamManager>();

    var split = Namespace.Split(request.Params?.Name ?? "");
    if (split is null)
    {
        throw new McpProtocolException(
            $"Prompt name '{request.Params?.Name}' is missing namespace prefix (expected format: slug__prompt_name)",
            McpErrorCode.InvalidParams);
    }

    var (slug, realName) = split.Value;
    if (!upstream.Connections.TryGetValue(slug, out var client))
    {
        throw new McpProtocolException(
            $"No upstream server with slug '{slug}'",
            McpErrorCode.InvalidParams);
    }

    var promptArgs = request.Params?.Arguments?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
    return await client.GetPromptAsync(realName, promptArgs, cancellationToken: ct);
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// Sync upstream connections in the background so the web UI is available immediately
_ = Task.Run(async () =>
{
    var upstream = app.Services.GetRequiredService<UpstreamManager>();
    await upstream.SyncAsync();
    app.Logger.LogInformation("Upstream connections synced");
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Host guard middleware — validates Host header for /mcp requests
app.UseMiddleware<HostGuardMiddleware>();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(DumbMcpMultiplexer.Client._Imports).Assembly);

// Map the MCP endpoint at /mcp
app.MapMcp("/mcp");

app.Run();
