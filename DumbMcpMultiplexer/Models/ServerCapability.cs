namespace DumbMcpMultiplexer.Models;

public class ServerCapability
{
    public const string ToolKind = "tool";

    public string ServerId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
    public string? SchemaJson { get; set; }
    public DateTime FetchedAt { get; set; }

    public McpServer Server { get; set; } = null!;
}
