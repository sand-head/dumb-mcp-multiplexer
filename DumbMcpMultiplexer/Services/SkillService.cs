using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using FuzzySharp;
using Microsoft.EntityFrameworkCore;

namespace DumbMcpMultiplexer.Services;

public class SkillService(AppDbContext db)
{
    public sealed class SkillSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
    }

    public async Task<List<SkillSummary>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.Skills
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new SkillSummary
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                UpdatedAt = s.UpdatedAt
            })
            .ToListAsync(ct);
    }

    public async Task<Skill?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        return await db.Skills
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == name, ct);
    }

    public async Task<Skill?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return await db.Skills
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>
    /// Searches skills by keyword using fuzzy matching on name and description.
    /// Returns results sorted by relevance score.
    /// </summary>
    public async Task<List<(Skill Skill, int Score)>> SearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        var allSkills = await db.Skills.AsNoTracking().ToListAsync(ct);

        if (string.IsNullOrWhiteSpace(query))
        {
            return allSkills
                .OrderBy(s => s.Name)
                .Take(limit)
                .Select(s => (s, 0))
                .ToList();
        }

        var queryLower = query.Trim().ToLowerInvariant();
        var scored = new List<(Skill Skill, int Score)>();

        foreach (var skill in allSkills)
        {
            var nameLower = skill.Name.ToLowerInvariant();
            var descLower = skill.Description.ToLowerInvariant();

            var nameScore = Fuzz.WeightedRatio(queryLower, nameLower);
            var descScore = Fuzz.WeightedRatio(queryLower, descLower);
            var combinedScore = nameScore * 2 + descScore;

            if (nameScore < 50 && descScore < 50)
                continue;

            scored.Add((skill, combinedScore));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Creates or updates a skill. If a skill with the given name already exists, it is updated.
    /// </summary>
    public async Task<Skill> CreateOrUpdateAsync(
        string name,
        string description,
        string code,
        IReadOnlyList<SkillArgument>? arguments = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Skill name is required.");
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Skill code is required.");

        var normalizedArguments = SkillArgumentsCodec.Normalize(arguments);

        var existing = await db.Skills.FirstOrDefaultAsync(s => s.Name == name, ct);
        if (existing is not null)
        {
            existing.Description = description?.Trim() ?? "";
            existing.Code = code;
            existing.Arguments = normalizedArguments;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var skill = new Skill
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Description = description?.Trim() ?? "",
            Arguments = normalizedArguments,
            Code = code,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        return skill;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (skill is null)
            return false;

        db.Skills.Remove(skill);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Skill?> UpdateAsync(
        string id,
        string name,
        string description,
        string code,
        IReadOnlyList<SkillArgument>? arguments,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Skill name is required.");
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Skill code is required.");

        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (skill is null)
            return null;

        var trimmedName = name.Trim();
        var existingByName = await db.Skills.FirstOrDefaultAsync(s => s.Name == trimmedName && s.Id != id, ct);
        if (existingByName is not null)
            throw new InvalidOperationException($"A skill named '{trimmedName}' already exists.");

        skill.Name = trimmedName;
        skill.Description = description?.Trim() ?? "";
        skill.Arguments = SkillArgumentsCodec.Normalize(arguments);
        skill.Code = code;
        skill.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return skill;
    }
}
