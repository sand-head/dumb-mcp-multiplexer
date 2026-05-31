namespace DumbMcpMultiplexer.Models;

public class ProfileServer
{
    public string ProfileId { get; set; } = string.Empty;
    public Profile? Profile { get; set; }

    public string ServerId { get; set; } = string.Empty;
    public McpServer? Server { get; set; }

    public bool Enabled { get; set; } = true;

    public ICollection<ProfileCapability> Capabilities { get; set; } = [];
}
