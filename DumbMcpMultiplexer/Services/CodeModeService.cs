using System.Text.Json;
using System.Text.Json.Nodes;
using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using FuzzySharp;
using Lua;
using Lua.Standard;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ToonNetSerializer;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Provides Code Mode: a per-profile feature that replaces direct tool exposure with
/// meta-tools for discovery and sandboxed Lua execution. Instead of calling tools directly,
/// LLMs search for tools, inspect their schemas, then write Lua code that chains tool calls.
/// </summary>
public class CodeModeService
{
    public const string SearchToolName = "search";
    public const string GetSchemaToolName = "get_schema";
    public const string ExecuteToolName = "execute";
    public const string CreateSkillToolName = "create_skill";
    public const string SearchSkillsToolName = "search_skills";

    public const int DefaultSearchLimit = 10;
    public const int MaxSearchLimit = 50;

    /// <summary>
    /// Returns the meta-tools exposed when Code Mode is enabled for a profile.
    /// <paramref name="servers"/> is the list of connected servers the profile has access to;
    /// names and slugs are both included in the search tool description so the LLM knows
    /// what's available and what values the 'server' filter accepts.
    /// </summary>
    public static IReadOnlyList<Tool> GetMetaTools(
        IReadOnlyList<(string Name, string Slug)>? servers = null,
        bool toonEnabled = false)
    {
        var serverList = servers is { Count: > 0 }
            ? $" Available servers: {string.Join(", ", servers.Select(s => $"{s.Name} ({s.Slug})"))}."
            : string.Empty;

        return
        [
            new Tool
            {
                Name = SearchToolName,
                Description = $"Search for available tools by keyword. Returns tool names and brief descriptions. Use this to discover what tools are available before writing code to call them. Results are ranked by relevance. If you get too many results, narrow your query or filter by server.{serverList}{(toonEnabled ? " Results are returned in TOON format." : "")}",
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
                            "description": "Optional: filter results to a specific server/namespace (accepts the server slug, display name, or full 'Name (slug)' label from the available servers list)"
                        },
                        "limit": {
                            "type": "integer",
                            "description": "Maximum number of results (default: 10, max: 50)",
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
                Name = GetSchemaToolName,
                Description = $"Get detailed parameter schemas for specific tools by name. Call this after searching to learn the exact parameters a tool expects before writing code that calls it. You can request multiple tools at once.{(toonEnabled ? " Schemas are returned in TOON format." : "")}",
                InputSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "tools": {
                            "type": "array",
                            "items": { "type": "string" },
                            "description": "List of fully-qualified tool names to get schemas for (e.g. ['github__create_issue', 'github__search_pull_requests'])"
                        },
                        "detail": {
                            "type": "string",
                            "enum": ["brief", "detailed", "full"],
                            "description": "Level of detail: 'brief' = names and descriptions only, 'detailed' = parameter names/types/required (default), 'full' = complete JSON schema"
                        }
                    },
                    "required": ["tools"]
                }
                """).RootElement
            },
            new Tool
            {
                Name = ExecuteToolName,
                Description = $"Execute Lua code in a sandbox. Inside the sandbox, `call_tool(name, args)` invokes any tool by its fully-qualified name, and `call_skill(name, args)` invokes a saved skill by name. Write a Lua script that chains tool/skill calls and returns the final result. Example:\n\nlocal result = call_tool(\"github__search_pull_requests\", {{ query = \"is:open author:me\" }})\nreturn result\n\nBoth functions take a name (string) and an arguments table. They return the result as a string. Use `return` to send the final result back.{(toonEnabled ? " Structured return values (objects and arrays) are automatically encoded in TOON format." : "")}",
                InputSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "code": {
                            "type": "string",
                            "description": "Lua code to execute. Use call_tool(name, args) to invoke tools. Use 'return' to return the final result."
                        }
                    },
                    "required": ["code"]
                }
                """).RootElement
            },
            new Tool
            {
                Name = CreateSkillToolName,
                Description = "Save a reusable Lua skill for future use. Skills are stored globally and can be invoked later via `call_skill(name, args)` inside execute. If a skill with the same name already exists, it will be updated. Use this to persist useful patterns, workflows, or utilities you've developed.",
                InputSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "name": {
                            "type": "string",
                            "description": "A short, unique identifier for the skill (e.g. 'summarize_pr', 'bulk_label_issues')"
                        },
                        "description": {
                            "type": "string",
                            "description": "A brief description of what the skill does, used for search/discovery"
                        },
                        "code": {
                            "type": "string",
                            "description": "The Lua code for the skill. Should use `...` (varargs) to accept arguments passed via call_skill. Has access to call_tool(name, args) and call_skill(name, args). Example: local args = ... ; return call_tool('github__get_issue', { owner = args.owner, repo = args.repo, issue_number = args.number })"
                        },
                        "arguments": {
                            "type": "array",
                            "description": "Optional argument definitions for call_skill(name, args), so clients know what to pass.",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "name": {
                                        "type": "string",
                                        "description": "Argument name (matches the key in args table)"
                                    },
                                    "type": {
                                        "type": "string",
                                        "description": "Expected data type (e.g. string, integer, boolean, object, array)"
                                    },
                                    "description": {
                                        "type": "string",
                                        "description": "Argument description, same style as tool parameter descriptions"
                                    },
                                    "required": {
                                        "type": "boolean",
                                        "description": "Whether this argument is required"
                                    }
                                },
                                "required": ["name"]
                            }
                        }
                    },
                    "required": ["name", "description", "code"]
                }
                """).RootElement
            },
            new Tool
            {
                Name = SearchSkillsToolName,
                Description = "Search for saved skills by keyword. Returns skill names and descriptions ranked by relevance. Use this to discover existing skills before writing new code or creating duplicate skills.",
                InputSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "query": {
                            "type": "string",
                            "description": "Search keywords to find relevant skills (e.g. 'summarize', 'github issues', 'label')"
                        },
                        "limit": {
                            "type": "integer",
                            "description": "Maximum number of results (default: 10, max: 50)",
                            "minimum": 1,
                            "maximum": 50
                        }
                    },
                    "required": ["query"]
                }
                """).RootElement
            }
        ];
    }

    /// <summary>
    /// Handles the 'search' meta-tool call. Returns matching tools ranked by relevance.
    /// </summary>
    public static async Task<CallToolResult> HandleSearchAsync(
        UpstreamManager upstream,
        AppDbContext db,
        string query,
        string? serverFilter,
        int limit,
        ProfileService.ActiveProfileContext profileContext,
        ILogger logger,
        bool toonEnabled,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxSearchLimit);
        var matchingTools = await GetMatchingToolsAsync(upstream, db, query, serverFilter, profileContext, logger, ct);
        var totalCount = matchingTools.Count;
        var pageTools = matchingTools.Take(limit).ToList();

        if (pageTools.Count == 0)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "No tools found matching your query. Try different keywords." }]
            };
        }

        if (toonEnabled)
        {
            var payload = new
            {
                total = totalCount,
                shown = pageTools.Count,
                tools = pageTools.Select(t => new { name = t.Name, description = t.Description ?? "" }).ToArray()
            };
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = ToonNet.Encode(payload) }]
            };
        }

        var lines = new List<string>();
        lines.Add($"{pageTools.Count} of {totalCount} tools:");
        lines.Add("");
        foreach (var tool in pageTools)
        {
            lines.Add($"- {tool.Name}: {tool.Description ?? "(no description)"}");
        }

        if (totalCount > limit)
        {
            lines.Add("");
            lines.Add($"({totalCount - limit} more results not shown — refine your query or increase limit)");
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = string.Join("\n", lines) }]
        };
    }

    /// <summary>
    /// Handles the 'get_schema' meta-tool call. Returns parameter schemas for the requested tools.
    /// </summary>
    public static async Task<CallToolResult> HandleGetSchemaAsync(
        UpstreamManager upstream,
        AppDbContext db,
        IReadOnlyList<string> toolNames,
        string detail,
        ProfileService.ActiveProfileContext profileContext,
        ILogger logger,
        bool toonEnabled,
        CancellationToken ct)
    {
        if (toolNames.Count == 0)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "No tool names provided. Pass a 'tools' array with tool names from search results." }]
            };
        }

        // Look up each requested tool from upstream servers
        var allTools = await GetMatchingToolsAsync(upstream, db, "", null, profileContext, logger, ct);
        var toolLookup = allTools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        if (toonEnabled)
        {
            var schemas = new List<object?>();
            foreach (var name in toolNames)
            {
                if (!toolLookup.TryGetValue(name, out var tool))
                {
                    schemas.Add(new { name, found = false, error = "Not found. Check the tool name is correct (use search to find tools)." });
                    continue;
                }

                if (detail == "brief")
                {
                    schemas.Add(new { name = tool.Name, description = tool.Description ?? "" });
                }
                else
                {
                    // "detailed" and "full" both emit the structured parameter list in TOON mode
                    var parameters = BuildParameterList(tool);
                    schemas.Add(new { name = tool.Name, description = tool.Description ?? "", parameters });
                }
            }
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = ToonNet.Encode(schemas) }]
            };
        }

        var sections = new List<string>();
        foreach (var name in toolNames)
        {
            if (!toolLookup.TryGetValue(name, out var tool))
            {
                sections.Add($"### {name}\n\nNot found. Check the tool name is correct (use search to find tools).");
                continue;
            }

            switch (detail)
            {
                case "brief":
                    sections.Add($"### {tool.Name}\n\n{tool.Description ?? "(no description)"}");
                    break;

                case "full":
                    var fullJson = tool.InputSchema.ValueKind != JsonValueKind.Undefined
                        ? JsonSerializer.Serialize(tool.InputSchema, new JsonSerializerOptions { WriteIndented = true })
                        : "{}";
                    sections.Add($"### {tool.Name}\n\n{tool.Description ?? "(no description)"}\n\n**Full JSON Schema**\n```json\n{fullJson}\n```");
                    break;

                case "detailed":
                default:
                    sections.Add(FormatDetailedSchema(tool));
                    break;
            }
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = string.Join("\n\n", sections) }]
        };
    }

    /// <summary>
    /// Handles the 'execute' meta-tool call. Runs Lua code in a sandbox with call_tool() available.
    /// </summary>
    public static async Task<CallToolResult> HandleExecuteAsync(
        UpstreamManager upstream,
        AppDbContext db,
        string code,
        ProfileService.ActiveProfileContext profileContext,
        ILogger logger,
        bool toonEnabled,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Error: no code provided." }],
                IsError = true
            };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var state = LuaState.Create();
            state.OpenBasicLibrary();
            state.OpenTableLibrary();
            state.OpenStringLibrary();
            state.OpenMathLibrary();
            state.OpenBitwiseLibrary();
            state.OpenCoroutineLibrary();

            // Register call_tool(name, args) function
            state.Environment["call_tool"] = new LuaFunction(async (context, luaCt) =>
            {
                var toolName = context.GetArgument<string>(0);
                var argsTable = context.HasArgument(1) ? context.GetArgument<LuaTable>(1) : null;

                if (string.IsNullOrEmpty(toolName))
                {
                    throw new LuaRuntimeException(context.State, new LuaValue("call_tool: first argument (tool name) is required"), 0);
                }

                // Resolve the tool's upstream server
                var split = Namespace.Split(toolName);
                if (split is null)
                {
                    throw new LuaRuntimeException(context.State, new LuaValue($"call_tool: tool name '{toolName}' is missing namespace prefix (expected format: slug__tool_name)"), 0);
                }

                var (slug, realName) = split.Value;

                // Check if enabled
                var toolDisabled = await db.ServerCapabilities
                    .AnyAsync(c => c.Kind == ServerCapability.ToolKind && !c.Enabled && c.Name == realName && c.Server.Slug == slug, cts.Token);
                if (!profileContext.IsCapabilityEnabled(slug, ServerCapability.ToolKind, realName, !toolDisabled))
                {
                    throw new LuaRuntimeException(context.State, new LuaValue($"call_tool: tool '{toolName}' is disabled"), 0);
                }

                if (!upstream.Connections.TryGetValue(slug, out var client))
                {
                    throw new LuaRuntimeException(context.State, new LuaValue($"call_tool: no upstream server with slug '{slug}'"), 0);
                }

                // Convert Lua table to Dictionary
                Dictionary<string, object?>? arguments = null;
                if (argsTable is not null)
                {
                    arguments = LuaTableToDictionary(argsTable);
                }

                logger.LogInformation("[CodeMode] call_tool: {ToolName} → upstream '{Slug}'", realName, slug);
                var callResult = await client.CallToolAsync(realName, arguments, cancellationToken: cts.Token);

                // Return the result as a string to Lua
                var resultText = string.Join("\n", callResult.Content
                    .Where(c => c is TextContentBlock)
                    .Cast<TextContentBlock>()
                    .Select(c => c.Text));

                return context.Return(new LuaValue(resultText));
            });

            // Register call_skill(name, args) function
            state.Environment["call_skill"] = new LuaFunction(async (context, luaCt) =>
            {
                var skillName = context.GetArgument<string>(0);
                var argsTable = context.HasArgument(1) ? context.GetArgument<LuaTable>(1) : null;

                if (string.IsNullOrEmpty(skillName))
                {
                    throw new LuaRuntimeException(context.State, new LuaValue("call_skill: first argument (skill name) is required"), 0);
                }

                var skill = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Name == skillName, cts.Token);
                if (skill is null)
                {
                    throw new LuaRuntimeException(context.State, new LuaValue($"call_skill: skill '{skillName}' not found"), 0);
                }

                logger.LogInformation("[CodeMode] call_skill: invoking '{SkillName}'", skillName);

                // Execute the skill's code in a nested Lua state that shares the same call_tool and call_skill functions
                var skillState = LuaState.Create();
                skillState.OpenBasicLibrary();
                skillState.OpenTableLibrary();
                skillState.OpenStringLibrary();
                skillState.OpenMathLibrary();
                skillState.OpenBitwiseLibrary();
                skillState.OpenCoroutineLibrary();
                skillState.Environment["call_tool"] = state.Environment["call_tool"];
                skillState.Environment["call_skill"] = state.Environment["call_skill"];

                // Pass arguments as varargs by wrapping the skill code
                var wrappedCode = skill.Code;
                if (argsTable is not null)
                {
                    // Make the args table available to the skill via the nested state
                    skillState.Environment["__skill_args"] = new LuaValue(argsTable);
                    wrappedCode = "local function __skill_fn(...)\n"
                        + skill.Code + "\n"
                        + "end\n"
                        + "return __skill_fn(__skill_args)";
                }
                else
                {
                    wrappedCode = "local function __skill_fn(...)\n"
                        + skill.Code + "\n"
                        + "end\n"
                        + "return __skill_fn()";
                }

                var skillResults = await skillState.DoStringAsync(wrappedCode, chunkName: $"skill:{skillName}", cancellationToken: cts.Token);
                var skillOutput = skillResults.Length > 0
                    ? LuaValueToString(skillResults[0])
                    : "";

                return context.Return(new LuaValue(skillOutput));
            });

            var results = await state.DoStringAsync(code, chunkName: "execute", cancellationToken: cts.Token);

            var output = results.Length > 0
                ? LuaValueToString(results[0])
                : "(no return value)";

            if (toonEnabled && output.Length > 0 && (output[0] == '{' || output[0] == '['))
            {
                try
                {
                    var jsonNode = JsonNode.Parse(output);
                    if (jsonNode is not null)
                        output = ToonNet.Encode(jsonNode);
                }
                catch { /* Not valid JSON — return as-is */ }
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = output }]
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Error: script execution timed out (30s limit)." }],
                IsError = true
            };
        }
        catch (LuaParseException ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Lua syntax error: {ex.Message}" }],
                IsError = true
            };
        }
        catch (LuaRuntimeException ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Lua runtime error: {ex.Message}" }],
                IsError = true
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[CodeMode] execute: unexpected error");
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }],
                IsError = true
            };
        }
    }

    /// <summary>
    /// Handles the 'create_skill' meta-tool call. Creates or updates a saved skill.
    /// </summary>
    public static async Task<CallToolResult> HandleCreateSkillAsync(
        AppDbContext db,
        string name,
        string description,
        string code,
        IReadOnlyList<SkillArgument>? arguments,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Error: skill name is required." }],
                IsError = true
            };
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Error: skill code is required." }],
                IsError = true
            };
        }

        try
        {
            var existing = await db.Skills.FirstOrDefaultAsync(s => s.Name == name.Trim(), ct);
            if (existing is not null)
            {
                existing.Description = description?.Trim() ?? "";
                existing.Code = code;
                existing.Arguments = SkillArgumentsCodec.Normalize(arguments);
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                logger.LogInformation("[CodeMode] create_skill: updated existing skill '{Name}'", name);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Skill '{name}' updated successfully." }]
                };
            }

            var skill = new Skill
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
                Description = description?.Trim() ?? "",
                Arguments = SkillArgumentsCodec.Normalize(arguments),
                Code = code,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Skills.Add(skill);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("[CodeMode] create_skill: created new skill '{Name}'", name);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Skill '{name}' created successfully." }]
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CodeMode] create_skill: failed for '{Name}'", name);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Error creating skill: {ex.Message}" }],
                IsError = true
            };
        }
    }

    /// <summary>
    /// Handles the 'search_skills' meta-tool call. Returns matching skills ranked by relevance.
    /// </summary>
    public static async Task<CallToolResult> HandleSearchSkillsAsync(
        AppDbContext db,
        string query,
        int limit,
        ILogger logger,
        bool toonEnabled,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxSearchLimit);

        var allSkills = await db.Skills.AsNoTracking().ToListAsync(ct);

        if (allSkills.Count == 0)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "No skills have been created yet. Use create_skill to save reusable Lua scripts." }]
            };
        }

        List<(Skill Skill, int Score)> scored;
        if (string.IsNullOrWhiteSpace(query))
        {
            scored = allSkills.OrderBy(s => s.Name).Take(limit).Select(s => (s, 0)).ToList();
        }
        else
        {
            var queryLower = query.Trim().ToLowerInvariant();
            scored = [];

            foreach (var skill in allSkills)
            {
                var nameLower = skill.Name.ToLowerInvariant();
                var descLower = skill.Description.ToLowerInvariant();

                var nameScore = Fuzz.WeightedRatio(queryLower, nameLower);
                var descScore = Fuzz.WeightedRatio(queryLower, descLower);
                var combinedScore = nameScore * 2 + descScore;

                if (nameScore < 50 && descScore < 50)
                    continue;

                scored.Add((skill, combinedScore));
            }

            scored = scored.OrderByDescending(s => s.Score).Take(limit).ToList();
        }

        if (scored.Count == 0)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "No skills found matching your query. Try different keywords or use create_skill to create a new one." }]
            };
        }

        if (toonEnabled)
        {
            var payload = new
            {
                total = scored.Count,
                skills = scored.Select(s => new
                {
                    name = s.Skill.Name,
                    description = s.Skill.Description,
                    arguments = s.Skill.Arguments.Select(a => new
                    {
                        name = a.Name,
                        type = a.Type,
                        description = a.Description,
                        required = a.Required
                    })
                }).ToArray()
            };
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = ToonNet.Encode(payload) }]
            };
        }

        var lines = new List<string>();
        lines.Add($"{scored.Count} skill(s) found:");
        lines.Add("");
        foreach (var (skill, _) in scored)
        {
            var arguments = skill.Arguments;
            var argumentText = arguments.Count == 0
                ? "no args"
                : string.Join(", ", arguments.Select(arg => $"{arg.Name}{(arg.Required ? "*" : "")}"));
            lines.Add($"- {skill.Name}: {skill.Description} (args: {argumentText})");
        }
        lines.Add("");
        lines.Add("Use call_skill(name, args) inside execute to invoke a skill.");

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = string.Join("\n", lines) }]
        };
    }

    /// <summary>
    /// Returns matching Tool objects from upstream servers, scored and sorted by relevance.
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
        HashSet<string>? allowedServerSlugs = null;

        if (!string.IsNullOrWhiteSpace(serverFilter))
        {
            var normalizedServerFilter = serverFilter.Trim().ToLowerInvariant();
            var connectedSlugsLower = connectedSlugs
                .Select(slug => slug.ToLowerInvariant())
                .ToList();
            allowedServerSlugs = (await db.Servers
                .Where(s => connectedSlugsLower.Contains(s.Slug.ToLower()) && (
                    s.Slug.ToLower() == normalizedServerFilter ||
                    s.Name.ToLower() == normalizedServerFilter ||
                    (s.Name + " (" + s.Slug + ")").ToLower() == normalizedServerFilter))
                .Select(server => server.Slug)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (allowedServerSlugs.Count == 0)
                return [];
        }

        foreach (var (slug, client) in upstream.Connections)
        {
            if (allowedServerSlugs is not null && !allowedServerSlugs.Contains(slug))
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

                    if (string.IsNullOrEmpty(queryLower))
                    {
                        scored.Add((prefixedTool, 0));
                        continue;
                    }

                    var nameLower = tool.Name.ToLowerInvariant();
                    var descLower = (tool.Description ?? "").ToLowerInvariant();

                    var nameScore = Fuzz.WeightedRatio(queryLower, nameLower);
                    var descScore = Fuzz.WeightedRatio(queryLower, descLower);
                    var combinedScore = nameScore * 2 + descScore;

                    if (nameScore < 50 && descScore < 50)
                        continue;

                    scored.Add((prefixedTool, combinedScore));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CodeMode] Failed to list tools from upstream: {Slug}", slug);
            }
        }

        if (string.IsNullOrEmpty(queryLower))
            return scored.Select(s => s.Tool).ToList();

        return scored.OrderByDescending(s => s.Score).Select(s => s.Tool).ToList();
    }

    private static List<object> BuildParameterList(Tool tool)
    {
        var parameters = new List<object>();
        if (tool.InputSchema.ValueKind == JsonValueKind.Object &&
            tool.InputSchema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            var requiredSet = new HashSet<string>(StringComparer.Ordinal);
            if (tool.InputSchema.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in required.EnumerateArray())
                {
                    if (r.GetString() is string rn)
                        requiredSet.Add(rn);
                }
            }

            foreach (var prop in properties.EnumerateObject())
            {
                var type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "any" : "any";
                var desc = prop.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                parameters.Add(new { name = prop.Name, type, required = requiredSet.Contains(prop.Name), description = desc });
            }
        }
        return parameters;
    }

    private static string FormatDetailedSchema(Tool tool)
    {
        var lines = new List<string>();
        lines.Add($"### {tool.Name}");
        lines.Add("");
        lines.Add(tool.Description ?? "(no description)");
        lines.Add("");
        lines.Add("**Parameters**");

        if (tool.InputSchema.ValueKind == JsonValueKind.Object &&
            tool.InputSchema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            var requiredSet = new HashSet<string>(StringComparer.Ordinal);
            if (tool.InputSchema.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in required.EnumerateArray())
                {
                    if (r.GetString() is string name)
                        requiredSet.Add(name);
                }
            }

            foreach (var prop in properties.EnumerateObject())
            {
                var type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "any" : "any";
                var isRequired = requiredSet.Contains(prop.Name);
                var desc = prop.Value.TryGetProperty("description", out var d) ? d.GetString() : null;
                var line = $"- `{prop.Name}` ({type}{(isRequired ? ", required" : "")})";
                if (!string.IsNullOrEmpty(desc))
                    line += $": {desc}";
                lines.Add(line);
            }
        }
        else
        {
            lines.Add("- (no parameters)");
        }

        return string.Join("\n", lines);
    }

    private static Dictionary<string, object?> LuaTableToDictionary(LuaTable table)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        var currentKey = LuaValue.Nil;
        while (table.TryGetNext(currentKey, out var pair))
        {
            var keyStr = LuaValueToString(pair.Key);
            dict[keyStr] = LuaValueToObject(pair.Value);
            currentKey = pair.Key;
        }
        return dict;
    }

    private static object? LuaValueToObject(LuaValue value)
    {
        if (value.TryRead<string>(out var str))
            return str;
        if (value.TryRead<double>(out var num))
        {
            // Return as int if it's a whole number
            if (num == Math.Floor(num) && num is >= int.MinValue and <= int.MaxValue)
                return (int)num;
            return num;
        }
        if (value.TryRead<bool>(out var b))
            return b;
        if (value.TryRead<LuaTable>(out var tbl))
        {
            // Check if it's an array (sequential integer keys starting from 1)
            var isArray = true;
            var maxIndex = 0;
            var currentKey = LuaValue.Nil;
            while (tbl.TryGetNext(currentKey, out var pair))
            {
                if (pair.Key.TryRead<double>(out var idx) && idx == Math.Floor(idx) && idx >= 1)
                {
                    maxIndex = Math.Max(maxIndex, (int)idx);
                }
                else
                {
                    isArray = false;
                    break;
                }
                currentKey = pair.Key;
            }

            if (isArray && maxIndex > 0)
            {
                var list = new List<object?>();
                for (int i = 1; i <= maxIndex; i++)
                {
                    list.Add(LuaValueToObject(tbl[i]));
                }
                return list;
            }

            return LuaTableToDictionary(tbl);
        }
        if (value == LuaValue.Nil)
            return null;

        return value.ToString();
    }

    private static string LuaValueToString(LuaValue value)
    {
        if (value.TryRead<string>(out var str))
            return str;
        if (value.TryRead<double>(out var num))
            return num.ToString();
        if (value.TryRead<bool>(out var b))
            return b.ToString().ToLowerInvariant();
        if (value.TryRead<LuaTable>(out var tbl))
            return JsonSerializer.Serialize(LuaTableToDictionary(tbl));
        return value.ToString() ?? "";
    }
}
