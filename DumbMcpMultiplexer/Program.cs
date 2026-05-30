using DumbMcpMultiplexer.Components;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Middleware;
using DumbMcpMultiplexer.Models;
using DumbMcpMultiplexer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Infrastructure", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}",
        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, config) => config
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Infrastructure", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}",
        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code));

// Add services to the container.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=app.db"));

builder.Services.AddScoped<ServerService>();
builder.Services.AddSingleton<UpstreamManager>();
builder.Services.AddSingleton<ContainerService>();
builder.Services.AddSingleton<ImageBuilderService>();
builder.Services.AddSingleton<DiscoveredToolsTracker>();
builder.Services.AddMemoryCache();

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
    var sessionId = request.Server.SessionId ?? "(no session)";

    logger.LogInformation("[{SessionId}] tools/call → {ToolName}", sessionId, toolName);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    // Handle progressive discovery meta-tools
    if (toolName == ProgressiveDiscoveryService.SearchToolName)
    {
        var query = request.Params?.Arguments?.TryGetValue("query", out var q) == true ? q.ToString() : "";
        var serverFilter2 = request.Params?.Arguments?.TryGetValue("server", out var s) == true ? s.ToString() : null;
        var offset = request.Params?.Arguments?.TryGetValue("offset", out var o) == true && int.TryParse(o.ToString(), out var ov) ? ov : 0;
        var limit = request.Params?.Arguments?.TryGetValue("limit", out var l) == true && int.TryParse(l.ToString(), out var lv) ? lv : ProgressiveDiscoveryService.DefaultPageSize;

        logger.LogInformation("[{SessionId}] search_tools: query={Query}, server={Server}, offset={Offset}, limit={Limit}",
            sessionId, query, serverFilter2 ?? "*", offset, limit);

        // Cache search results so subsequent pages of the same query are fast
        var cache = request.Services!.GetRequiredService<IMemoryCache>();
        var cacheKey = $"search:{query}:{serverFilter2 ?? "*"}";
        if (!cache.TryGetValue<IReadOnlyList<Tool>>(cacheKey, out var matchingTools) || matchingTools is null)
        {
            logger.LogInformation("[{SessionId}] search_tools: cache miss, querying upstreams...", sessionId);
            matchingTools = await ProgressiveDiscoveryService.GetMatchingToolsAsync(upstream, db, query, serverFilter2, logger, ct);
            cache.Set(cacheKey, matchingTools, TimeSpan.FromMinutes(2));
            logger.LogInformation("[{SessionId}] search_tools: found {Count} matching tools in {Elapsed}ms",
                sessionId, matchingTools.Count, sw.ElapsedMilliseconds);
        }
        else
        {
            logger.LogInformation("[{SessionId}] search_tools: cache hit ({Count} tools)", sessionId, matchingTools.Count);
        }

        var (result, pageTools) = ProgressiveDiscoveryService.FormatSearchResult(matchingTools, offset, limit);

        // Register only the tools in this page so they appear in subsequent tools/list responses
        if (await ProgressiveDiscoveryService.IsEnabledAsync(db, ct) && pageTools.Count > 0)
        {
            var tracker = request.Services!.GetRequiredService<DiscoveredToolsTracker>();
            var sid = request.Server.SessionId ?? "";
            if (tracker.RegisterDiscoveredTools(sid, pageTools))
            {
                logger.LogInformation("[{SessionId}] search_tools: registered {Count} new tools, sending tools/list_changed",
                    sessionId, pageTools.Count);
                await request.Server.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, ct);
            }
        }

        return result;
    }

    if (toolName == ProgressiveDiscoveryService.ListCategoriesToolName)
    {
        logger.LogInformation("[{SessionId}] list_tool_categories", sessionId);
        var catResult = await ProgressiveDiscoveryService.HandleListCategoriesAsync(upstream, logger, ct);
        logger.LogInformation("[{SessionId}] list_tool_categories completed in {Elapsed}ms", sessionId, sw.ElapsedMilliseconds);
        return catResult;
    }

    if (toolName == ProgressiveDiscoveryService.CallToolName)
    {
        var targetName = request.Params?.Arguments?.TryGetValue("name", out var n) == true ? n.ToString() : "";
        logger.LogInformation("[{SessionId}] call_tool: name={TargetTool}", sessionId, targetName);

        if (string.IsNullOrEmpty(targetName))
        {
            throw new McpProtocolException(
                "The 'name' argument is required for call_tool",
                McpErrorCode.InvalidParams);
        }

        var targetSplit = Namespace.Split(targetName);
        if (targetSplit is null)
        {
            throw new McpProtocolException(
                $"Tool name '{targetName}' is missing namespace prefix (expected format: slug__tool_name)",
                McpErrorCode.InvalidParams);
        }

        var (targetSlug, targetRealName) = targetSplit.Value;

        var targetDisabled = await db.ServerCapabilities
            .AnyAsync(c => c.Kind == ServerCapability.ToolKind && !c.Enabled && c.Name == targetRealName && c.Server.Slug == targetSlug, ct);
        if (targetDisabled)
        {
            throw new McpProtocolException(
                $"Tool '{targetName}' is disabled by multiplexer configuration",
                McpErrorCode.InvalidParams);
        }

        if (!upstream.Connections.TryGetValue(targetSlug, out var targetClient))
        {
            throw new McpProtocolException(
                $"No upstream server with slug '{targetSlug}'",
                McpErrorCode.InvalidParams);
        }

        // Extract the nested arguments object.
        // Prefer 'arguments' when it is a non-empty object; fall back to 'arguments_json' so that
        // clients that cannot populate an object field don't get silenced by an accidental empty {} value.
        Dictionary<string, object?>? targetArgs = null;
        if (request.Params?.Arguments?.TryGetValue("arguments", out var argsElement) == true
            && argsElement is System.Text.Json.JsonElement jsonEl
            && jsonEl.ValueKind == System.Text.Json.JsonValueKind.Object
            && jsonEl.EnumerateObject().Any())
        {
            targetArgs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(jsonEl.GetRawText());
        }
        else if (request.Params?.Arguments?.TryGetValue("arguments_json", out var argsJsonElement) == true)
        {
            var argsJson = argsJsonElement.ToString();
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                try
                {
                    targetArgs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    logger.LogWarning("[{SessionId}] call_tool: failed to parse arguments_json: {Error}", sessionId, ex.Message);
                    throw new McpProtocolException(
                        $"Invalid JSON in arguments_json: {ex.Message}",
                        McpErrorCode.InvalidParams);
                }
            }
        }

        logger.LogInformation("[{SessionId}] call_tool: forwarding {RealName} \u2192 upstream '{Slug}'", sessionId, targetRealName, targetSlug);
        try
        {
            var callResult = await targetClient.CallToolAsync(targetRealName, targetArgs, cancellationToken: ct);
            logger.LogInformation("[{SessionId}] call_tool: {TargetTool} completed in {Elapsed}ms", sessionId, targetName, sw.ElapsedMilliseconds);
            return callResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{SessionId}] call_tool: {TargetTool} FAILED after {Elapsed}ms", sessionId, targetName, sw.ElapsedMilliseconds);
            throw;
        }
    }

    var split = Namespace.Split(toolName);
    if (split is null)
    {
        logger.LogWarning("[{SessionId}] tools/call: invalid tool name (no namespace): {ToolName}", sessionId, toolName);
        throw new McpProtocolException(
            $"Tool name '{request.Params?.Name}' is missing namespace prefix (expected format: slug__tool_name)",
            McpErrorCode.InvalidParams);
    }

    var (slug, realName) = split.Value;

    var toolDisabled = await db.ServerCapabilities
        .AnyAsync(c => c.Kind == ServerCapability.ToolKind && !c.Enabled && c.Name == realName && c.Server.Slug == slug, ct);
    if (toolDisabled)
    {
        logger.LogWarning("[{SessionId}] tools/call: tool disabled: {ToolName}", sessionId, toolName);
        throw new McpProtocolException(
            $"Tool '{request.Params?.Name}' is disabled by multiplexer configuration",
            McpErrorCode.InvalidParams);
    }

    if (!upstream.Connections.TryGetValue(slug, out var client))
    {
        logger.LogWarning("[{SessionId}] tools/call: no upstream for slug '{Slug}'", sessionId, slug);
        throw new McpProtocolException(
            $"No upstream server with slug '{slug}'",
            McpErrorCode.InvalidParams);
    }

    logger.LogInformation("[{SessionId}] tools/call: forwarding {RealName} → upstream '{Slug}'", sessionId, realName, slug);
    var arguments = request.Params?.Arguments?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
    try
    {
        var callResult = await client.CallToolAsync(realName, arguments, cancellationToken: ct);
        logger.LogInformation("[{SessionId}] tools/call: {ToolName} completed in {Elapsed}ms", sessionId, toolName, sw.ElapsedMilliseconds);
        return callResult;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[{SessionId}] tools/call: {ToolName} FAILED after {Elapsed}ms", sessionId, toolName, sw.ElapsedMilliseconds);
        throw;
    }
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

var containerService = app.Services.GetRequiredService<ContainerService>();
await containerService.InitializeAsync();

// Apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Clear any stale migration lock from a previous unclean shutdown.
    // Safe for single-instance deployments; SQLite file-level locking prevents actual concurrency issues.
    await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsLock\"");
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
