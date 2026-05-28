using System.Text.Json;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
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
                Description = "Search for available tools by keyword. Use this to discover tools that can help with your current task. Returns matching tool names, descriptions, and their input schemas.",
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
                        }
                    },
                    "required": ["query"]
                }
                """).RootElement
            },
            new Tool
            {
                Name = ListCategoriesToolName,
                Description = "List all available tool categories (upstream servers). Use this to understand what tool domains are available before searching for specific tools.",
                InputSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
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
    /// Handles the search_tools meta-tool call. Returns matching tools from upstream servers.
    /// </summary>
    public static async Task<CallToolResult> HandleSearchToolsAsync(
        UpstreamManager upstream,
        AppDbContext db,
        string query,
        string? serverFilter,
        ILogger logger,
        CancellationToken ct)
    {
        var connectedSlugs = upstream.Connections.Keys.ToList();

        // Get disabled tools lookup
        var disabledToolLookup = await db.ServerCapabilities
            .Where(c => c.Kind == ServerCapability.ToolKind && !c.Enabled && connectedSlugs.Contains(c.Server.Slug))
            .Select(c => new { c.Server.Slug, c.Name })
            .ToListAsync(ct);
        var disabledToolsBySlug = disabledToolLookup
            .GroupBy(x => x.Slug, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(x => x.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<object>();

        foreach (var (slug, client) in upstream.Connections)
        {
            if (serverFilter is not null && !slug.Equals(serverFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var upstreamTools = await client.ListToolsAsync(cancellationToken: ct);
                foreach (var tool in upstreamTools)
                {
                    if (disabledToolsBySlug.TryGetValue(slug, out var disabledTools) && disabledTools.Contains(tool.Name))
                        continue;

                    // Match against tool name and description
                    var searchText = $"{tool.Name} {tool.Description}".ToLowerInvariant();
                    var matches = keywords.Length == 0 || keywords.Any(k => searchText.Contains(k.ToLowerInvariant()));

                    if (matches)
                    {
                        results.Add(new
                        {
                            name = Namespace.Prefix(slug, tool.Name),
                            description = tool.Description,
                            inputSchema = tool.JsonSchema
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list tools from upstream during search: {Slug}", slug);
            }
        }

        var responseText = results.Count > 0
            ? JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })
            : "No tools found matching your query. Try different keywords or use 'list_tool_categories' to see available servers.";

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = responseText }]
        };
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
                    toolCount = upstreamTools.Count,
                    tools = upstreamTools.Select(t => t.Name).ToList()
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list tools from upstream during category listing: {Slug}", slug);
                categories.Add(new
                {
                    server = slug,
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
