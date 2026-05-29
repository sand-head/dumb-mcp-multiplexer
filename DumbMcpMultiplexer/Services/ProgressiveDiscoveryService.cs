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
    /// Returns matching Tool objects from upstream servers (for registering in the discovered tools tracker).
    /// This is the same filtering logic as HandleSearchToolsAsync but returns Tool protocol objects.
    /// </summary>
    public static async Task<IReadOnlyList<Tool>> GetMatchingToolsAsync(
        UpstreamManager upstream,
        AppDbContext db,
        string query,
        string? serverFilter,
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

        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLowerInvariant())
            .ToArray();
        var scored = new List<(Tool Tool, double Score)>();

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

                    // Empty query matches everything with no scoring
                    if (keywords.Length == 0)
                    {
                        scored.Add((new Tool
                        {
                            Name = Namespace.Prefix(slug, tool.Name),
                            Description = tool.Description,
                            InputSchema = tool.JsonSchema
                        }, 0));
                        continue;
                    }

                    var nameLower = tool.Name.ToLowerInvariant();
                    var descLower = (tool.Description ?? "").ToLowerInvariant();

                    double totalScore = 0;
                    int matchedCount = 0;

                    foreach (var keyword in keywords)
                    {
                        double keywordScore = 0;

                        // Score against name (higher weight)
                        if (nameLower.Contains(keyword))
                        {
                            keywordScore += IsWordBoundaryMatch(nameLower, keyword) ? 10.0 : 5.0;
                        }

                        // Score against description (lower weight)
                        if (descLower.Contains(keyword))
                        {
                            keywordScore += IsWordBoundaryMatch(descLower, keyword) ? 4.0 : 2.0;
                        }

                        if (keywordScore > 0)
                        {
                            matchedCount++;
                            totalScore += keywordScore;
                        }
                    }

                    // Must match at least one keyword
                    if (matchedCount == 0)
                        continue;

                    // Bonus for matching more keywords (ratio of matched to total)
                    double coverageRatio = (double)matchedCount / keywords.Length;
                    // Strong multiplier for full coverage, graceful degradation for partial
                    totalScore *= 0.5 + (coverageRatio * 0.5);
                    // Extra bonus when ALL keywords match
                    if (matchedCount == keywords.Length)
                        totalScore *= 1.5;

                    scored.Add((new Tool
                    {
                        Name = Namespace.Prefix(slug, tool.Name),
                        Description = tool.Description,
                        InputSchema = tool.JsonSchema
                    }, totalScore));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list tools from upstream during search: {Slug}", slug);
            }
        }

        // If no keywords, return as-is (no sorting); otherwise sort by score descending
        if (keywords.Length == 0)
            return scored.Select(s => s.Tool).ToList();

        return scored.OrderByDescending(s => s.Score).Select(s => s.Tool).ToList();
    }

    /// <summary>
    /// Checks whether a keyword appears at a word boundary in the text.
    /// Word boundaries are defined by non-alphanumeric characters (underscores, hyphens, spaces, etc.)
    /// or transitions that naturally separate tokens in tool names.
    /// </summary>
    private static bool IsWordBoundaryMatch(string text, string keyword)
    {
        int index = 0;
        while (true)
        {
            index = text.IndexOf(keyword, index, StringComparison.Ordinal);
            if (index < 0)
                return false;

            bool startBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            int end = index + keyword.Length;
            bool endBoundary = end >= text.Length || !char.IsLetterOrDigit(text[end]);

            if (startBoundary || endBoundary)
                return true;

            index++;
        }
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
        ILogger logger,
        CancellationToken ct)
    {
        var matchingTools = await GetMatchingToolsAsync(upstream, db, query, serverFilter, logger, ct);
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
