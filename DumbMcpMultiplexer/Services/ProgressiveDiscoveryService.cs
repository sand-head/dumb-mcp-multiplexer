using System.Collections.Concurrent;
using System.Text.Json;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Provides progressive/dynamic tool discovery so that downstream LLM context
/// does not balloon when many tools are available. Instead of exposing all tools,
/// we expose meta-tools that allow the LLM to search and discover tools on demand.
/// </summary>
public class ProgressiveDiscoveryService
{
    public const string SettingKey = "progressive_discovery";
    public const string SearchToolName = "search_tools";
    public const string ListCategoriesToolName = "list_tool_categories";
    public const string CallToolName = "call_tool";

    /// <summary>
    /// Returns the meta-tools that are exposed when progressive discovery is enabled.
    /// </summary>
    public static IReadOnlyList<Tool> GetMetaTools()
    {
        return
        [
            new Tool
            {
                Name = SearchToolName,
                Description = "Discover available tools by searching with keywords. Always call this before call_tool — it returns the exact tool names and argument schemas you'll need. Accepts multiple space-separated keywords; results are ranked by relevance. If you get too many results, narrow your query or filter by server. If you get too few, broaden your keywords or paginate with offset/limit.",
                InputSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "query": {
                            "type": "string",
                            "description": "Search keywords to find relevant tools (e.g. 'create issue', 'file search', 'database')"
                        },
                        "server": {
                            "type": "string",
                            "description": "Optional: filter results to a specific server/category slug"
                        },
                        "offset": {
                            "type": "integer",
                            "description": "Starting index for paginated results (default: 0)",
                            "minimum": 0
                        },
                        "limit": {
                            "type": "integer",
                            "description": "Maximum number of results to return (default: 20, max: 50)",
                            "minimum": 1,
                            "maximum": 50
                        }
                    },
                    "required": ["query"]
                }
                """).RootElement
            },
            new Tool
            {
                Name = ListCategoriesToolName,
                Description = "List the upstream servers connected to this proxy, including their names, descriptions, and tool counts. Call this first if you're unsure which server handles a task (e.g. is it 'github' or 'gitlab'?), then use search_tools with the server filter to narrow results.",
                InputSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """).RootElement
            },
            new Tool
            {
                Name = CallToolName,
                Description = "Invoke any tool by its fully-qualified name. You must call search_tools first to get the tool name and its argument schema — do not guess tool names. Pass the exact name returned by search_tools (e.g. 'github__create_issue') and an arguments object matching that tool's input schema. Use 'arguments' if you can pass a structured object; if your client can only send string fields, use 'arguments_json' instead — it is used as a fallback when 'arguments' is absent or empty. Example: call_tool(name: 'github__search_pull_requests', arguments: {query: 'is:open assignee:me'}).",
                InputSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "name": {
                            "type": "string",
                            "description": "The fully-qualified tool name (e.g. 'github__create_issue') as returned by search_tools"
                        },
                        "arguments_json": {
                            "type": "string",
                            "description": "Fallback: tool arguments as a JSON string, used when 'arguments' is absent or an empty object. Example: \"{\\\"query\\\": \\\"is:open assignee:me\\\"}\""
                        },
                        "arguments": {
                            "type": "object",
                            "description": "The arguments to pass to the tool as an object (preferred). Falls back to 'arguments_json' when this is absent or empty. Example: {\"query\": \"is:open assignee:me\"}"
                        }
                    },
                    "required": ["name"]
                }
                """).RootElement
            }
        ];
    }

    /// <summary>
    /// Checks whether progressive discovery mode is enabled.
    /// </summary>
    public static async Task<bool> IsEnabledAsync(AppDbContext db, CancellationToken ct = default)
    {
        var setting = await db.Settings.FindAsync([SettingKey], ct);
        return setting?.Value == "true";
    }

    /// <summary>
    /// Returns matching Tool objects from upstream servers, scored and sorted by relevance using
    /// fuzzy string matching (FuzzySharp). Both tool name and description are scored separately;
    /// name matches are weighted 2:1 over description matches.
    /// </summary>
    public static async Task<IReadOnlyList<Tool>> GetMatchingToolsAsync(
        UpstreamManager upstream,
        AppDbContext db,
        string query,
        string? serverFilter,
        ProfileService.ActiveProfileContext profileContext,
        ILogger logger,
        CancellationToken ct)
    {
        var connectedSlugs = upstream.Connections.Keys.ToList();

        var disabledToolLookup = await db.ServerCapabilities
            .Where(c => c.Kind == ServerCapability.ToolKind && !c.Enabled && connectedSlugs.Contains(c.Server.Slug))
            .Select(c => new { c.Server.Slug, c.Name })
            .ToListAsync(ct);
        var disabledToolsBySlug = disabledToolLookup
            .GroupBy(x => x.Slug, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(x => x.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        var scored = new List<(Tool Tool, int Score)>();
        var queryLower = query.Trim().ToLowerInvariant();

        foreach (var (slug, client) in upstream.Connections)
        {
            if (serverFilter is not null && !slug.Equals(serverFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!profileContext.IsServerEnabled(slug))
                continue;

            try
            {
                var upstreamTools = await client.ListToolsAsync(cancellationToken: ct);
                foreach (var tool in upstreamTools)
                {
                    var globalEnabled = !(disabledToolsBySlug.TryGetValue(slug, out var disabledTools) && disabledTools.Contains(tool.Name));
                    if (!profileContext.IsCapabilityEnabled(slug, ServerCapability.ToolKind, tool.Name, globalEnabled))
                        continue;

                    var prefixedTool = new Tool
                    {
                        Name = Namespace.Prefix(slug, tool.Name),
                        Description = tool.Description,
                        InputSchema = JsonSchemaSanitizer.Sanitize(tool.JsonSchema)
                    };

                    // Empty query: return everything unsorted
                    if (string.IsNullOrEmpty(queryLower))
                    {
                        scored.Add((prefixedTool, 0));
                        continue;
                    }

                    var nameLower = tool.Name.ToLowerInvariant();
                    var descLower = (tool.Description ?? "").ToLowerInvariant();

                    // WeightedRatio handles both full and partial matching intelligently.
                    // Name is weighted 2:1 over description — a query matching the tool's
                    // name is a stronger signal than matching its prose description.
                    var nameScore = Fuzz.WeightedRatio(queryLower, nameLower);
                    var descScore = Fuzz.WeightedRatio(queryLower, descLower);
                    var combinedScore = nameScore * 2 + descScore;

                    // Require at least one meaningful signal to avoid surfacing total noise
                    if (nameScore < 50 && descScore < 50)
                        continue;

                    scored.Add((prefixedTool, combinedScore));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list tools from upstream during search: {Slug}", slug);
            }
        }

        if (string.IsNullOrEmpty(queryLower))
            return scored.Select(s => s.Tool).ToList();

        return scored.OrderByDescending(s => s.Score).Select(s => s.Tool).ToList();
    }

    /// <summary>
    /// Formats a list of matching tools into a CallToolResult for the search_tools response,
    /// applying pagination and including metadata about total results.
    /// Returns the paginated subset of tools that were included in the response.
    /// </summary>
    public static (CallToolResult Result, IReadOnlyList<Tool> PageTools) FormatSearchResult(
        IReadOnlyList<Tool> matchingTools, int offset = 0, int limit = DefaultPageSize)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        offset = Math.Max(offset, 0);

        var totalCount = matchingTools.Count;
        var pageTools = matchingTools.Skip(offset).Take(limit).ToList();
        var hasMore = offset + limit < totalCount;

        if (pageTools.Count == 0 && totalCount == 0)
        {
            return (new CallToolResult
            {
                Content = [new TextContentBlock { Text = "No tools found matching your query. Try different keywords or use 'list_tool_categories' to see available servers." }]
            }, []);
        }

        var response = new
        {
            tools = pageTools.Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                inputSchema = tool.InputSchema
            }).ToList(),
            pagination = new
            {
                offset,
                limit,
                total = totalCount,
                hasMore,
                nextOffset = hasMore ? offset + limit : (int?)null
            }
        };

        var responseText = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });

        return (new CallToolResult
        {
            Content = [new TextContentBlock { Text = responseText }]
        }, pageTools);
    }

    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>
    /// Handles the search_tools meta-tool call. Returns matching tools from upstream servers.
    /// </summary>
    public static async Task<CallToolResult> HandleSearchToolsAsync(
        UpstreamManager upstream,
        AppDbContext db,
        string query,
        string? serverFilter,
        ProfileService.ActiveProfileContext profileContext,
        ILogger logger,
        CancellationToken ct)
    {
        var matchingTools = await GetMatchingToolsAsync(upstream, db, query, serverFilter, profileContext, logger, ct);
        return FormatSearchResult(matchingTools).Result;
    }

    /// <summary>
    /// Handles the list_tool_categories meta-tool call. Returns available upstream servers with tool counts.
    /// </summary>
    public static async Task<CallToolResult> HandleListCategoriesAsync(
        UpstreamManager upstream,
        ILogger logger,
        CancellationToken ct)
    {
        var categories = new List<object>();

        foreach (var (slug, client) in upstream.Connections)
        {
            try
            {
                var upstreamTools = await client.ListToolsAsync(cancellationToken: ct);
                categories.Add(new
                {
                    server = slug,
                    name = client.ServerInfo?.Title ?? client.ServerInfo?.Name ?? slug,
                    description = client.ServerInfo?.Description,
                    toolCount = upstreamTools.Count
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list tools from upstream during category listing: {Slug}", slug);
                categories.Add(new
                {
                    server = slug,
                    name = slug,
                    description = (string?)null,
                    toolCount = 0,
                    tools = new List<string>()
                });
            }
        }

        var responseText = categories.Count > 0
            ? JsonSerializer.Serialize(categories, new JsonSerializerOptions { WriteIndented = true })
            : "No upstream servers are currently connected.";

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = responseText }]
        };
    }
}
