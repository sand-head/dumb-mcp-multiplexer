using DumbMcpMultiplexer.Models;
using System.Text.Json;

namespace DumbMcpMultiplexer.Services;

public static class SkillArgumentsCodec
{
    public static string Serialize(IReadOnlyList<SkillArgument>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "[]";

        var normalized = arguments
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new SkillArgument
            {
                Name = a.Name.Trim(),
                Type = string.IsNullOrWhiteSpace(a.Type) ? "string" : a.Type.Trim(),
                Description = a.Description?.Trim() ?? "",
                Required = a.Required
            })
            .ToList();

        return JsonSerializer.Serialize(normalized);
    }

    public static List<SkillArgument> Deserialize(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<SkillArgument>>(argumentsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
