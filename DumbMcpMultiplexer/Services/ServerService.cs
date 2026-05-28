using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore;

namespace DumbMcpMultiplexer.Services;

public class ServerService(AppDbContext db)
{
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
