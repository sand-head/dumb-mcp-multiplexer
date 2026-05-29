using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Tracks which tools have been discovered per MCP session via the search_tools meta-tool.
/// When progressive discovery is enabled, only meta-tools are initially exposed via tools/list.
/// After a search_tools call discovers tools, they are registered here so that subsequent
/// tools/list responses include them — making them actually callable by the client.
/// </summary>
public sealed class DiscoveredToolsTracker
{
    // SessionId → set of discovered Tool objects
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Tool>> _discovered = new();

    /// <summary>
    /// Registers tools as discovered for the given session.
    /// Returns true if any NEW tools were added (i.e., the tool list actually changed).
    /// </summary>
    public bool RegisterDiscoveredTools(string sessionId, IEnumerable<Tool> tools)
    {
        var sessionTools = _discovered.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, Tool>(StringComparer.Ordinal));
        var anyNew = false;

        foreach (var tool in tools)
        {
            if (sessionTools.TryAdd(tool.Name, tool))
            {
                anyNew = true;
            }
        }

        return anyNew;
    }

    /// <summary>
    /// Gets all tools previously discovered for the given session.
    /// </summary>
    public IReadOnlyList<Tool> GetDiscoveredTools(string sessionId)
    {
        if (_discovered.TryGetValue(sessionId, out var sessionTools))
        {
            return sessionTools.Values.ToList();
        }

        return [];
    }

    /// <summary>
    /// Removes all discovered tools for a session (for cleanup on disconnect).
    /// </summary>
    public void ClearSession(string sessionId)
    {
        _discovered.TryRemove(sessionId, out _);
    }
}
