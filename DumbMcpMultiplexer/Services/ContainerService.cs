using Docker.DotNet;

namespace DumbMcpMultiplexer.Services;

public sealed class ContainerService(IConfiguration configuration, ILogger<ContainerService> logger) : IAsyncDisposable
{
    private DockerClient? _client;

    public DockerClient? Client => _client;
    public bool IsAvailable { get; private set; }
    public string? SocketPath { get; private set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            return;
        }

        var configuredSocket = configuration["ContainerSocket"] ?? configuration["CONTAINER_SOCKET"];
        string[] socketCandidates = string.IsNullOrWhiteSpace(configuredSocket)
            ? ["/var/run/docker.sock", "/run/podman/podman.sock"]
            : [configuredSocket];

        foreach (var socket in socketCandidates)
        {
            try
            {
                var client = new DockerClientConfiguration(new Uri($"unix://{socket}")).CreateClient();
                await client.System.PingAsync(ct);
                _client = client;
                SocketPath = socket;
                IsAvailable = true;
                logger.LogInformation("Connected to container socket at {SocketPath}", socket);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to connect to container socket at {SocketPath}", socket);
            }
        }

        IsAvailable = false;
        SocketPath = socketCandidates.FirstOrDefault();
    }

    public ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }
        return ValueTask.CompletedTask;
    }
}
