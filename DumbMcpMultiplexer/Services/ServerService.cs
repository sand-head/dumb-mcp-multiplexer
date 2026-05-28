using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore;

namespace DumbMcpMultiplexer.Services;

public class ServerService(AppDbContext db)
{
    private const string ToolCapabilityKind = "tool";

    public async Task<List<McpServer>> GetAllServersAsync()
    {
        return await db.Servers.OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<List<McpServer>> GetEnabledServersAsync()
    {
        return await db.Servers.Where(s => s.Enabled).OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<McpServer?> GetServerBySlugAsync(string slug)
    {
        return await db.Servers.FirstOrDefaultAsync(s => s.Slug == slug);
    }

    public async Task<McpServer?> GetServerByIdAsync(string id)
    {
        return await db.Servers.FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<McpServer> CreateServerAsync(string slug, string name, string? url, string headersJson)
    {
        var server = new McpServer
        {
            Id = Guid.NewGuid().ToString(),
            Slug = slug,
            Name = name,
            Url = url,
            Headers = headersJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Servers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    public async Task UpdateServerAsync(string id, string slug, string name, string? url, string headersJson)
    {
        var server = await db.Servers.FindAsync(id)
            ?? throw new InvalidOperationException($"Server '{id}' not found");

        server.Slug = slug;
        server.Name = name;
        server.Url = url;
        server.Headers = headersJson;
        server.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
    }

    public async Task DeleteServerAsync(string id)
    {
        var server = await db.Servers.FindAsync(id);
        if (server is not null)
        {
            db.Servers.Remove(server);
            await db.SaveChangesAsync();
        }
    }

    public async Task<bool> ToggleServerEnabledAsync(string id)
    {
        var server = await db.Servers.FindAsync(id)
            ?? throw new InvalidOperationException($"Server '{id}' not found");

        server.Enabled = !server.Enabled;
        server.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return server.Enabled;
    }

    public async Task<bool> SlugExistsAsync(string slug, string? excludeId = null)
    {
        return await db.Servers.AnyAsync(s => s.Slug == slug && (excludeId == null || s.Id != excludeId));
    }

    public async Task<List<ServerCapability>> GetToolCapabilitiesAsync(string serverId)
    {
        return await db.ServerCapabilities
            .Where(c => c.ServerId == serverId && c.Kind == ToolCapabilityKind)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task SyncToolCapabilitiesAsync(string serverId, IEnumerable<(string Name, string? Description, string? SchemaJson)> tools)
    {
        var incomingTools = tools
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var existingTools = await db.ServerCapabilities
            .Where(c => c.ServerId == serverId && c.Kind == ToolCapabilityKind)
            .ToListAsync();

        foreach (var existing in existingTools.Where(c => !incomingTools.ContainsKey(c.Name)))
        {
            db.ServerCapabilities.Remove(existing);
        }

        foreach (var incoming in incomingTools.Values)
        {
            var existing = existingTools.FirstOrDefault(c => c.Name == incoming.Name);
            if (existing is null)
            {
                db.ServerCapabilities.Add(new ServerCapability
                {
                    ServerId = serverId,
                    Kind = ToolCapabilityKind,
                    Name = incoming.Name,
                    Enabled = true,
                    Description = incoming.Description,
                    SchemaJson = incoming.SchemaJson,
                    FetchedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.Description = incoming.Description;
                existing.SchemaJson = incoming.SchemaJson;
                existing.FetchedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task ToggleToolCapabilityEnabledAsync(string serverId, string toolName)
    {
        var capability = await db.ServerCapabilities.FirstOrDefaultAsync(c =>
            c.ServerId == serverId && c.Kind == ToolCapabilityKind && c.Name == toolName)
            ?? throw new InvalidOperationException($"Tool capability '{toolName}' not found for server '{serverId}'");

        capability.Enabled = !capability.Enabled;
        capability.FetchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // Settings helpers
    public async Task<string?> GetSettingAsync(string key)
    {
        var setting = await db.Settings.FindAsync(key);
        return setting?.Value;
    }

    public async Task SetSettingAsync(string key, string value)
    {
        var setting = await db.Settings.FindAsync(key);
        if (setting is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
        await db.SaveChangesAsync();
    }
}
