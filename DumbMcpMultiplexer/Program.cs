using DumbMcpMultiplexer.Components;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Middleware;
using DumbMcpMultiplexer.Models;
using DumbMcpMultiplexer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
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

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>();

builder.Services.AddScoped<ServerService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<SkillService>();
builder.Services.AddSingleton<ProfileChangeNotifier>();
builder.Services.AddSingleton<UpstreamManager>();
builder.Services.AddSingleton<ContainerService>();
builder.Services.AddSingleton<ImageBuilderService>();

builder.Services.AddHostedService<UpstreamHealthCheckService>();
builder.Services.AddSingleton<StdioLifecycleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StdioLifecycleService>());
builder.Services.AddHttpContextAccessor();

static string? GetEndpointProfile(IServiceProvider services)
{
    var httpContextAccessor = services.GetRequiredService<IHttpContextAccessor>();
    return httpContextAccessor.HttpContext?.Request.RouteValues.TryGetValue("profile", out var profileValue) == true
        ? profileValue?.ToString()
        : null;
}

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
        var notifier = httpContext.RequestServices.GetRequiredService<ProfileChangeNotifier>();
        using var _ = notifier.Subscribe(async () =>
        {
            if (!ct.IsCancellationRequested)
                await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);
        });
        await server.RunAsync(ct);
    };
#pragma warning restore MCPEXP002
})
.WithListToolsHandler(async (request, ct) =>
{
    var upstream = request.Services!.GetRequiredService<UpstreamManager>();
    var db = request.Services!.GetRequiredService<AppDbContext>();
    var profileService = request.Services!.GetRequiredService<ProfileService>();
    var logger = request.Services!.GetRequiredService<ILogger<Program>>();
    var profileContext = await profileService.GetProfileContextAsync(GetEndpointProfile(request.Services!), ct);

    // If Code Mode is enabled for this profile, return only meta-tools (search, get_schema, execute)
    if (profileContext.CodeModeEnabled)
    {
        var enabledConnectedSlugs = upstream.Connections.Keys
            .Where(profileContext.IsServerEnabled)
            .ToHashSet();
        var servers = await db.Servers
            .Where(s => enabledConnectedSlugs.Contains(s.Slug))
            .OrderBy(s => s.Name)
            .Select(s => new { s.Name, s.Slug })
            .ToListAsync(ct);
        var serverInfos = servers.Select(s => (s.Name, s.Slug)).ToList();
        return new ListToolsResult { Tools = CodeModeService.GetMetaTools(serverInfos, profileContext.CodeModeToonEnabled).ToList() };
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
        if (!profileContext.IsServerEnabled(slug))
        {
            continue;
        }

        try
        {
            var upstreamTools = await client.ListToolsAsync(cancellationToken: ct);
            foreach (var tool in upstreamTools)
            {
                var globalEnabled = !(disabledToolsBySlug.TryGetValue(slug, out var disabledTools) && disabledTools.Contains(tool.Name));
                if (!profileContext.IsCapabilityEnabled(slug, ServerCapability.ToolKind, tool.Name, globalEnabled))
                {
                    continue;
                }

                tools.Add(new Tool
                {
                    Name = Namespace.Prefix(slug, tool.Name),
                    Description = tool.Description,
                    InputSchema = JsonSchemaSanitizer.Sanitize(tool.JsonSchema)
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
    var profileService = request.Services!.GetRequiredService<ProfileService>();
    var logger = request.Services!.GetRequiredService<ILogger<Program>>();
    var profileContext = await profileService.GetProfileContextAsync(GetEndpointProfile(request.Services!), ct);
    var toolName = request.Params?.Name ?? "";
    var sessionId = request.Server.SessionId ?? "(no session)";

    logger.LogInformation("[{SessionId}] tools/call → {ToolName}", sessionId, toolName);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    // Handle Code Mode meta-tools when the profile has code mode enabled
    if (profileContext.CodeModeEnabled)
    {
        if (toolName == CodeModeService.SearchToolName)
        {
            var query = request.Params?.Arguments?.TryGetValue("query", out var q) == true ? q.ToString() : "";
            var serverFilter2 = request.Params?.Arguments?.TryGetValue("server", out var s) == true ? s.ToString() : null;
            var limit = request.Params?.Arguments?.TryGetValue("limit", out var l) == true && int.TryParse(l.ToString(), out var lv) ? lv : CodeModeService.DefaultSearchLimit;

            logger.LogInformation("[{SessionId}] code_mode/search: query={Query}, server={Server}, limit={Limit}",
                sessionId, query, serverFilter2 ?? "*", limit);

            var result = await CodeModeService.HandleSearchAsync(upstream, db, query, serverFilter2, limit, profileContext, logger, profileContext.CodeModeToonEnabled, ct);
            logger.LogInformation("[{SessionId}] code_mode/search completed in {Elapsed}ms", sessionId, sw.ElapsedMilliseconds);
            return result;
        }

        if (toolName == CodeModeService.GetSchemaToolName)
        {
            var toolNames = new List<string>();
            if (request.Params?.Arguments?.TryGetValue("tools", out var toolsArg) == true)
            {
                if (toolsArg is System.Text.Json.JsonElement jsonArr && jsonArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in jsonArr.EnumerateArray())
                    {
                        if (item.GetString() is string name)
                            toolNames.Add(name);
                    }
                }
            }
            var detail = request.Params?.Arguments?.TryGetValue("detail", out var d) == true ? d.ToString() ?? "detailed" : "detailed";

            logger.LogInformation("[{SessionId}] code_mode/get_schema: tools=[{Tools}], detail={Detail}",
                sessionId, string.Join(", ", toolNames), detail);

            var result = await CodeModeService.HandleGetSchemaAsync(upstream, db, toolNames, detail, profileContext, logger, profileContext.CodeModeToonEnabled, ct);
            logger.LogInformation("[{SessionId}] code_mode/get_schema completed in {Elapsed}ms", sessionId, sw.ElapsedMilliseconds);
            return result;
        }

        if (toolName == CodeModeService.ExecuteToolName)
        {
            var code = request.Params?.Arguments?.TryGetValue("code", out var c) == true ? c.ToString() : "";
            logger.LogInformation("[{SessionId}] code_mode/execute: code length={Length}", sessionId, code?.Length ?? 0);

            var result = await CodeModeService.HandleExecuteAsync(upstream, db, code ?? "", profileContext, logger, profileContext.CodeModeToonEnabled, ct);
            logger.LogInformation("[{SessionId}] code_mode/execute completed in {Elapsed}ms", sessionId, sw.ElapsedMilliseconds);
            return result;
        }

        if (toolName == CodeModeService.CreateSkillToolName)
        {
            var skillName = request.Params?.Arguments?.TryGetValue("name", out var n) == true ? n.ToString() ?? "" : "";
            var skillDesc = request.Params?.Arguments?.TryGetValue("description", out var desc) == true ? desc.ToString() ?? "" : "";
            var skillCode = request.Params?.Arguments?.TryGetValue("code", out var sc) == true ? sc.ToString() ?? "" : "";

            logger.LogInformation("[{SessionId}] code_mode/create_skill: name={Name}", sessionId, skillName);

            var result = await CodeModeService.HandleCreateSkillAsync(db, skillName, skillDesc, skillCode, logger, ct);
            logger.LogInformation("[{SessionId}] code_mode/create_skill completed in {Elapsed}ms", sessionId, sw.ElapsedMilliseconds);
            return result;
        }

        if (toolName == CodeModeService.SearchSkillsToolName)
        {
            var skillQuery = request.Params?.Arguments?.TryGetValue("query", out var sq) == true ? sq.ToString() ?? "" : "";
            var skillLimit = request.Params?.Arguments?.TryGetValue("limit", out var sl) == true && int.TryParse(sl.ToString(), out var slv) ? slv : CodeModeService.DefaultSearchLimit;

            logger.LogInformation("[{SessionId}] code_mode/search_skills: query={Query}, limit={Limit}", sessionId, skillQuery, skillLimit);

            var result = await CodeModeService.HandleSearchSkillsAsync(db, skillQuery, skillLimit, logger, profileContext.CodeModeToonEnabled, ct);
            logger.LogInformation("[{SessionId}] code_mode/search_skills completed in {Elapsed}ms", sessionId, sw.ElapsedMilliseconds);
            return result;
        }

        // If code mode is enabled but the tool isn't a meta-tool, it shouldn't be callable directly
        throw new McpProtocolException(
            $"Tool '{toolName}' is not available in Code Mode. Use 'search' to find tools, then 'execute' to call them via Lua code.",
            McpErrorCode.InvalidParams);
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
    if (!profileContext.IsCapabilityEnabled(slug, ServerCapability.ToolKind, realName, !toolDisabled))
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
    var db = request.Services!.GetRequiredService<AppDbContext>();
    var profileService = request.Services!.GetRequiredService<ProfileService>();
    var logger = request.Services!.GetRequiredService<ILogger<Program>>();
    var profileContext = await profileService.GetProfileContextAsync(GetEndpointProfile(request.Services!), ct);
    var resources = new List<Resource>();
    var connectedSlugs = upstream.Connections.Keys.ToList();

    var disabledResourceLookup = await db.ServerCapabilities
        .Where(c => c.Kind == ServerCapability.ResourceKind && !c.Enabled && connectedSlugs.Contains(c.Server.Slug))
        .Select(c => new { c.Server.Slug, c.Name })
        .ToListAsync(ct);
    var disabledResourcesBySlug = disabledResourceLookup
        .GroupBy(x => x.Slug, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(x => x.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    foreach (var (slug, client) in upstream.Connections)
    {
        if (!profileContext.IsServerEnabled(slug))
        {
            continue;
        }

        try
        {
            var upstreamResources = await client.ListResourcesAsync(cancellationToken: ct);
            foreach (var resource in upstreamResources)
            {
                var resourceName = resource.Uri;
                var globalEnabled = !(disabledResourcesBySlug.TryGetValue(slug, out var disabledResources) && disabledResources.Contains(resourceName));
                if (!profileContext.IsCapabilityEnabled(slug, ServerCapability.ResourceKind, resourceName, globalEnabled))
                {
                    continue;
                }

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
    var db = request.Services!.GetRequiredService<AppDbContext>();
    var profileService = request.Services!.GetRequiredService<ProfileService>();
    var profileContext = await profileService.GetProfileContextAsync(GetEndpointProfile(request.Services!), ct);

    var split = Namespace.SplitUri(request.Params?.Uri ?? "");
    if (split is null)
    {
        throw new McpProtocolException(
            $"Resource URI '{request.Params?.Uri}' is missing namespace prefix (expected format: proxy://slug/uri)",
            McpErrorCode.InvalidParams);
    }

    var (slug, realUri) = split.Value;
    var resourceDisabled = await db.ServerCapabilities
        .AnyAsync(c => c.Kind == ServerCapability.ResourceKind && !c.Enabled && c.Name == realUri && c.Server.Slug == slug, ct);
    if (!profileContext.IsCapabilityEnabled(slug, ServerCapability.ResourceKind, realUri, !resourceDisabled))
    {
        throw new McpProtocolException(
            $"Resource '{request.Params?.Uri}' is disabled by multiplexer configuration",
            McpErrorCode.InvalidParams);
    }

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
    var db = request.Services!.GetRequiredService<AppDbContext>();
    var profileService = request.Services!.GetRequiredService<ProfileService>();
    var logger = request.Services!.GetRequiredService<ILogger<Program>>();
    var profileContext = await profileService.GetProfileContextAsync(GetEndpointProfile(request.Services!), ct);
    var prompts = new List<Prompt>();
    var connectedSlugs = upstream.Connections.Keys.ToList();

    var disabledPromptLookup = await db.ServerCapabilities
        .Where(c => c.Kind == ServerCapability.PromptKind && !c.Enabled && connectedSlugs.Contains(c.Server.Slug))
        .Select(c => new { c.Server.Slug, c.Name })
        .ToListAsync(ct);
    var disabledPromptsBySlug = disabledPromptLookup
        .GroupBy(x => x.Slug, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(x => x.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    foreach (var (slug, client) in upstream.Connections)
    {
        if (!profileContext.IsServerEnabled(slug))
        {
            continue;
        }

        try
        {
            var upstreamPrompts = await client.ListPromptsAsync(cancellationToken: ct);
            foreach (var prompt in upstreamPrompts)
            {
                var globalEnabled = !(disabledPromptsBySlug.TryGetValue(slug, out var disabledPrompts) && disabledPrompts.Contains(prompt.Name));
                if (!profileContext.IsCapabilityEnabled(slug, ServerCapability.PromptKind, prompt.Name, globalEnabled))
                {
                    continue;
                }

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
    var db = request.Services!.GetRequiredService<AppDbContext>();
    var profileService = request.Services!.GetRequiredService<ProfileService>();
    var profileContext = await profileService.GetProfileContextAsync(GetEndpointProfile(request.Services!), ct);

    var split = Namespace.Split(request.Params?.Name ?? "");
    if (split is null)
    {
        throw new McpProtocolException(
            $"Prompt name '{request.Params?.Name}' is missing namespace prefix (expected format: slug__prompt_name)",
            McpErrorCode.InvalidParams);
    }

    var (slug, realName) = split.Value;
    var promptDisabled = await db.ServerCapabilities
        .AnyAsync(c => c.Kind == ServerCapability.PromptKind && !c.Enabled && c.Name == realName && c.Server.Slug == slug, ct);
    if (!profileContext.IsCapabilityEnabled(slug, ServerCapability.PromptKind, realName, !promptDisabled))
    {
        throw new McpProtocolException(
            $"Prompt '{request.Params?.Name}' is disabled by multiplexer configuration",
            McpErrorCode.InvalidParams);
    }

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

// CORS middleware — adds Access-Control headers for /mcp requests from allowed browser origins
app.UseMiddleware<McpCorsMiddleware>();

// Host guard middleware — validates Host header for /mcp requests
app.UseMiddleware<HostGuardMiddleware>();

// API key middleware — enforces Bearer token auth on /mcp when a key is configured
app.UseMiddleware<ApiKeyMiddleware>();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(DumbMcpMultiplexer.Client._Imports).Assembly);

// Map MCP endpoints at /mcp and /mcp/{profile}
app.MapMcp("/mcp");
app.MapMcp("/mcp/{profile}");

app.Run();
