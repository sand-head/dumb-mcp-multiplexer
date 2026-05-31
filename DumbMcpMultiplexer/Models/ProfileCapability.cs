namespace DumbMcpMultiplexer.Models;

public class ProfileCapability
{
    public string ProfileId { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public ProfileServer? ProfileServer { get; set; }

    public string Kind { get; set; } = "tool";
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
