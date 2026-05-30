using System.Security.Cryptography;
using System.Text;
using Docker.DotNet.Models;
using DumbMcpMultiplexer.Models;
using ICSharpCode.SharpZipLib.Tar;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Builds container images from Containerfile content for stdio_container MCP servers.
/// Builds via the Docker socket API using an in-memory tar context and caches by content hash.
/// </summary>
public sealed class ImageBuilderService(
    ContainerService containerService,
    ILogger<ImageBuilderService> logger)
{
    /// <summary>
    /// Ensures an image exists for the given server configuration.
    /// For pre-built images, returns the image reference directly.
    /// For Containerfile builds, checks cache by content hash and builds if needed.
    /// </summary>
    public async Task<string> EnsureImageAsync(McpServer server, CancellationToken ct = default)
    {
        // Pre-built image: return directly
        if (!string.IsNullOrWhiteSpace(server.ContainerImage))
        {
            return server.ContainerImage;
        }

        // Containerfile build
        if (!string.IsNullOrWhiteSpace(server.Containerfile))
        {
            var contentHash = ComputeContentHash(server.Containerfile);
            var imageTag = $"dumb-mcp-local/{server.Slug}:{contentHash}";

            if (await ImageExistsAsync(imageTag, ct))
            {
                logger.LogInformation("Image cache hit for {Slug}: {ImageTag}", server.Slug, imageTag);
                return imageTag;
            }

            logger.LogInformation("Building image for {Slug}: {ImageTag}", server.Slug, imageTag);
            await BuildImageAsync(imageTag, server.Containerfile, ct);
            logger.LogInformation("Successfully built image for {Slug}: {ImageTag}", server.Slug, imageTag);

            return imageTag;
        }

        throw new InvalidOperationException(
            $"Server '{server.Slug}': stdio_container transport requires either ContainerImage or Containerfile.");
    }

    /// <summary>
    /// Forces a rebuild of the image for the given server, ignoring cache.
    /// Only works for servers with a Containerfile.
    /// </summary>
    public async Task<string> RebuildImageAsync(McpServer server, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server.Containerfile))
        {
            throw new InvalidOperationException("Rebuild is only available for servers with a Containerfile.");
        }

        var contentHash = ComputeContentHash(server.Containerfile);
        var imageTag = $"dumb-mcp-local/{server.Slug}:{contentHash}";

        logger.LogInformation("Rebuilding image for {Slug}: {ImageTag}", server.Slug, imageTag);
        await BuildImageAsync(imageTag, server.Containerfile, ct);
        logger.LogInformation("Successfully rebuilt image for {Slug}: {ImageTag}", server.Slug, imageTag);

        return imageTag;
    }

    internal static string ComputeContentHash(string containerfileContent)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(containerfileContent));
        return Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
    }

    private async Task<bool> ImageExistsAsync(string imageTag, CancellationToken ct)
    {
        if (!containerService.IsAvailable || containerService.Client is null)
        {
            return false;
        }

        try
        {
            var images = await containerService.Client.Images.ListImagesAsync(
                new ImagesListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["reference"] = new Dictionary<string, bool> { [imageTag] = true }
                    }
                }, ct);

            return images.Count > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check if image exists: {ImageTag}", imageTag);
            return false;
        }
    }

    private async Task BuildImageAsync(string imageTag, string containerfile, CancellationToken ct)
    {
        if (!containerService.IsAvailable || containerService.Client is null)
        {
            throw new InvalidOperationException("Container runtime socket is unavailable.");
        }

        var tarStream = CreateBuildContext(containerfile);

        try
        {
            await containerService.Client.Images.BuildImageFromDockerfileAsync(
                new ImageBuildParameters
                {
                    Tags = [imageTag],
                    Dockerfile = "Containerfile",
                    Remove = true,
                    ForceRemove = true
                },
                tarStream,
                authConfigs: null,
                headers: null,
                progress: new BuildProgress(logger),
                ct);
        }
        finally
        {
            await tarStream.DisposeAsync();
        }
    }

    private static MemoryStream CreateBuildContext(string containerfile)
    {
        var memoryStream = new MemoryStream();
        using (var tarOutputStream = new TarOutputStream(memoryStream, Encoding.UTF8) { IsStreamOwner = false })
        {
            var containerfileBytes = Encoding.UTF8.GetBytes(containerfile);
            var entry = TarEntry.CreateTarEntry("Containerfile");
            entry.Size = containerfileBytes.Length;
            tarOutputStream.PutNextEntry(entry);
            tarOutputStream.Write(containerfileBytes, 0, containerfileBytes.Length);
            tarOutputStream.CloseEntry();
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private sealed class BuildProgress(ILogger logger) : IProgress<JSONMessage>
    {
        public void Report(JSONMessage value)
        {
            if (!string.IsNullOrWhiteSpace(value.Stream))
            {
                logger.LogDebug("Build: {Message}", value.Stream.TrimEnd());
            }
            else if (!string.IsNullOrWhiteSpace(value.ErrorMessage))
            {
                logger.LogError("Build error: {Error}", value.ErrorMessage);
            }
        }
    }
}
