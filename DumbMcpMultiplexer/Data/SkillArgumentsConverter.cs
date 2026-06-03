using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace DumbMcpMultiplexer.Data;

public class SkillArgumentsConverter() : ValueConverter<List<SkillArgument>, string>(
    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
    v => JsonSerializer.Deserialize<List<SkillArgument>>(v, (JsonSerializerOptions?)null) ?? new List<SkillArgument>())
{
}
