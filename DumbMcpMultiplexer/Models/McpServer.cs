namespace DumbMcpMultiplexer.Models;

public class McpServer
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = "remote_http";
    public bool Enabled { get; set; } = true;
    public string? Url { get; set; }
    public string Headers { get; set; } = "{}";
    public string? Command { get; set; }
    public string? Args { get; set; }
    public string? Env { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ServerCapability> Capabilities { get; set; } = [];
}
