using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using DumbMcpMultiplexer.Models;
using ICSharpCode.SharpZipLib.Tar;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Builds container images from runtime templates for Tier 2 (auto-build) MCP servers.
/// Generates a Containerfile, builds via the Docker socket API using an in-memory tar context,
/// and caches the result by content hash.
/// </summary>
public sealed class ImageBuilderService(
    ContainerService containerService,
    ILogger<ImageBuilderService> logger)
{
    private static readonly Dictionary<string, RuntimeTemplate> RuntimeTemplates = new()
    {
        ["node"] = new RuntimeTemplate("node:22-slim", "npm install -g {packages}"),
        ["python"] = new RuntimeTemplate("python:3.12-slim", "pip install --no-cache-dir {packages}"),
        ["uvx"] = new RuntimeTemplate("ghcr.io/astral-sh/uv:python3.12-slim", "uv pip install --system {packages}"),
    };

    /// <summary>
    /// Ensures an image exists for the given server configuration.
    /// For Tier 1 (pre-built image), returns the image reference directly.
    /// For Tier 2 (auto-build), checks cache by content hash and builds if needed.
    /// </summary>
    public async Task<string> EnsureImageAsync(McpServer server, CancellationToken ct = default)
    {
        // Tier 1: pre-built image
        if (!string.IsNullOrWhiteSpace(server.ContainerImage))
        {
            return server.ContainerImage;
        }

        // Tier 2: auto-build
        if (string.IsNullOrWhiteSpace(server.ContainerRuntime))
        {
            throw new InvalidOperationException(
                "Container transport requires either a container image (Tier 1) or a runtime (Tier 2).");
        }

        if (!RuntimeTemplates.TryGetValue(server.ContainerRuntime, out var template))
        {
            throw new InvalidOperationException(
                $"Unsupported container runtime: '{server.ContainerRuntime}'. Supported: {string.Join(", ", RuntimeTemplates.Keys)}");
        }

        var packages = ParsePackages(server.ContainerPackages);
        var containerfile = GenerateContainerfile(template, packages, server.Command, server.Args);
        var contentHash = ComputeContentHash(server.ContainerRuntime, packages, server.Command, server.Args, template.BaseImage);
        var imageTag = $"dumb-mcp-local/{server.Slug}:{contentHash}";

        // Check if image already exists in cache
        if (await ImageExistsAsync(imageTag, ct))
        {
            logger.LogInformation("Image cache hit for {Slug}: {ImageTag}", server.Slug, imageTag);
            return imageTag;
        }

        // Build the image
        logger.LogInformation("Building image for {Slug}: {ImageTag}", server.Slug, imageTag);
        await BuildImageAsync(imageTag, containerfile, ct);
        logger.LogInformation("Successfully built image for {Slug}: {ImageTag}", server.Slug, imageTag);

        return imageTag;
    }

    /// <summary>
    /// Forces a rebuild of the image for the given server, ignoring cache.
    /// </summary>
    public async Task<string> RebuildImageAsync(McpServer server, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server.ContainerRuntime))
        {
            throw new InvalidOperationException("Rebuild is only available for Tier 2 (auto-build) servers.");
        }

        if (!RuntimeTemplates.TryGetValue(server.ContainerRuntime, out var template))
        {
            throw new InvalidOperationException(
                $"Unsupported container runtime: '{server.ContainerRuntime}'.");
        }

        var packages = ParsePackages(server.ContainerPackages);
        var containerfile = GenerateContainerfile(template, packages, server.Command, server.Args);
        var contentHash = ComputeContentHash(server.ContainerRuntime, packages, server.Command, server.Args, template.BaseImage);
        var imageTag = $"dumb-mcp-local/{server.Slug}:{contentHash}";

        logger.LogInformation("Rebuilding image for {Slug}: {ImageTag}", server.Slug, imageTag);
        await BuildImageAsync(imageTag, containerfile, ct);
        logger.LogInformation("Successfully rebuilt image for {Slug}: {ImageTag}", server.Slug, imageTag);

        return imageTag;
    }

    /// <summary>
    /// Gets the supported runtime IDs.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedRuntimes => RuntimeTemplates.Keys.ToList();

    internal static string GenerateContainerfile(RuntimeTemplate template, List<string> packages, string? command, string? argsJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FROM {template.BaseImage}");

        if (packages.Count > 0)
        {
            var packagesStr = string.Join(" ", packages);
            sb.AppendLine($"RUN {template.InstallCommand.Replace("{packages}", packagesStr)}");
        }

        var entrypoint = BuildEntrypointJson(command, argsJson);
        if (entrypoint is not null)
        {
            sb.AppendLine($"ENTRYPOINT {entrypoint}");
        }

        return sb.ToString();
    }

    internal static string ComputeContentHash(string runtime, List<string> packages, string? command, string? argsJson, string baseImage)
    {
        var sortedPackages = packages.OrderBy(p => p).ToList();
        var hashInput = $"{runtime}|{baseImage}|{string.Join(",", sortedPackages)}|{command ?? ""}|{argsJson ?? ""}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
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

    private static List<string> ParsePackages(string? packagesJson)
    {
        if (string.IsNullOrWhiteSpace(packagesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(packagesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? BuildEntrypointJson(string? command, string? argsJson)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(command))
        {
            parts.Add(command.Trim());
        }

        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                var args = JsonSerializer.Deserialize<List<string>>(argsJson) ?? [];
                parts.AddRange(args.Where(a => !string.IsNullOrWhiteSpace(a)));
            }
            catch (JsonException)
            {
                // ignore
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(parts);
    }

    internal sealed record RuntimeTemplate(string BaseImage, string InstallCommand);

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
