using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DumbMcpMultiplexer.Data;

public class SkillArgumentsComparer() : ValueComparer<List<SkillArgument>>(
    (a, b) => a != null && b != null
        && a.Count == b.Count
        && a.Zip(b).All(p =>
            p.First.Name == p.Second.Name &&
            p.First.Type == p.Second.Type &&
            p.First.Description == p.Second.Description &&
            p.First.Required == p.Second.Required),
    v => v.Aggregate(0, (h, e) => HashCode.Combine(h, e.Name, e.Type, e.Description, e.Required)),
    v => v.Select(e => new SkillArgument
    {
        Name = e.Name,
        Type = e.Type,
        Description = e.Description,
        Required = e.Required
    }).ToList())
{
}
