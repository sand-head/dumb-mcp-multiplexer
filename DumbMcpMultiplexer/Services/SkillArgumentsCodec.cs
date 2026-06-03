using DumbMcpMultiplexer.Models;

namespace DumbMcpMultiplexer.Services;

public static class SkillArgumentsCodec
{
    /// <summary>
    /// Normalizes a raw argument list: trims whitespace, drops unnamed entries,
    /// and defaults missing types to "string".
    /// </summary>
    public static List<SkillArgument> Normalize(IReadOnlyList<SkillArgument>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return [];

        return arguments
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new SkillArgument
            {
                Name = a.Name.Trim(),
                Type = string.IsNullOrWhiteSpace(a.Type) ? "string" : a.Type.Trim(),
                Description = a.Description?.Trim() ?? "",
                Required = a.Required
            })
            .ToList();
    }
}
