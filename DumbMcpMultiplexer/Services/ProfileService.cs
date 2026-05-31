using DumbMcpMultiplexer.Data;
using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore;

namespace DumbMcpMultiplexer.Services;

public class ProfileService(AppDbContext db)
{
    public const string ActiveProfileIdSettingKey = "active_profile_id";

    public sealed class ActiveProfileContext
    {
        public string? ProfileId { get; init; }
        public string? ProfileName { get; init; }
        public HashSet<string> EnabledServerSlugs { get; init; } = [];
        public Dictionary<(string Slug, string Kind, string Name), bool> CapabilityOverrides { get; init; } = [];
        public bool HasActiveProfile => !string.IsNullOrWhiteSpace(ProfileId);

        public bool IsServerEnabled(string slug)
        {
            return !HasActiveProfile || EnabledServerSlugs.Contains(slug);
        }

        public bool IsCapabilityEnabled(string slug, string kind, string name, bool globalEnabled)
        {
            if (!globalEnabled || !IsServerEnabled(slug))
            {
                return false;
            }

            if (!HasActiveProfile)
            {
                return true;
            }

            return !CapabilityOverrides.TryGetValue((slug, kind, name), out var enabled) || enabled;
        }
    }

    public sealed class ProfileServerEdit
    {
        public string ServerId { get; set; } = string.Empty;
        public string ServerSlug { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public bool Included { get; set; }
        public bool Enabled { get; set; } = true;
        public List<CapabilityEdit> ToolCapabilities { get; set; } = [];
    }

    public sealed class CapabilityEdit
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    public sealed class ProfileEditModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public List<ProfileServerEdit> Servers { get; set; } = [];
    }

    public sealed class ProfileSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public bool IsActive { get; set; }
        public int IncludedServerCount { get; set; }
    }

    public async Task<string?> GetActiveProfileIdAsync(CancellationToken cancellationToken = default)
    {
        var setting = await db.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == ActiveProfileIdSettingKey, cancellationToken);

        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return null;
        }

        var profileExists = await db.Profiles
            .AsNoTracking()
            .AnyAsync(p => p.Id == setting.Value, cancellationToken);

        return profileExists ? setting.Value : null;
    }

    public async Task<string?> GetActiveProfileNameAsync(CancellationToken cancellationToken = default)
    {
        var activeProfileId = await GetActiveProfileIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(activeProfileId))
        {
            return null;
        }

        return await db.Profiles
            .AsNoTracking()
            .Where(p => p.Id == activeProfileId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SetActiveProfileAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var exists = await db.Profiles.AnyAsync(p => p.Id == profileId, cancellationToken);
            if (!exists)
            {
                throw new InvalidOperationException("Profile not found.");
            }
        }

        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == ActiveProfileIdSettingKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            if (setting is not null)
            {
                db.Settings.Remove(setting);
                await db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (setting is null)
        {
            db.Settings.Add(new AppSetting
            {
                Key = ActiveProfileIdSettingKey,
                Value = profileId
            });
        }
        else
        {
            setting.Value = profileId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ActiveProfileContext> GetActiveProfileContextAsync(CancellationToken cancellationToken = default)
    {
        var activeProfileId = await GetActiveProfileIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(activeProfileId))
        {
            return new ActiveProfileContext();
        }

        return await GetProfileContextByIdAsync(activeProfileId, cancellationToken);
    }

    public async Task<ActiveProfileContext> GetProfileContextAsync(string? profileReference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileReference))
        {
            return new ActiveProfileContext();
        }

        var normalizedReference = profileReference.Trim();
        var normalizedUpper = normalizedReference.ToUpperInvariant();
        var profile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name.ToUpper() == normalizedUpper, cancellationToken);

        if (profile is null)
        {
            return new ActiveProfileContext();
        }

        return await GetProfileContextAsync(profile, cancellationToken);
    }

    private async Task<ActiveProfileContext> GetProfileContextByIdAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return new ActiveProfileContext();
        }

        return await GetProfileContextAsync(profile, cancellationToken);
    }

    private async Task<ActiveProfileContext> GetProfileContextAsync(Profile profile, CancellationToken cancellationToken = default)
    {

        var profileServers = await db.ProfileServers
            .AsNoTracking()
            .Where(ps => ps.ProfileId == profile.Id)
            .Include(ps => ps.Server)
            .ToListAsync(cancellationToken);

        var serverById = profileServers
            .Where(ps => ps.Server is not null)
            .ToDictionary(ps => ps.ServerId, ps => ps.Server!, StringComparer.Ordinal);

        var enabledServerSlugs = profileServers
            .Where(ps => ps.Enabled && ps.Server is not null && ps.Server.Enabled)
            .Select(ps => ps.Server!.Slug)
            .ToHashSet(StringComparer.Ordinal);

        var capabilityOverrides = new Dictionary<(string Slug, string Kind, string Name), bool>();
        var profileCapabilities = await db.ProfileCapabilities
            .AsNoTracking()
            .Where(pc => pc.ProfileId == profile.Id)
            .ToListAsync(cancellationToken);

        foreach (var capability in profileCapabilities)
        {
            if (!serverById.TryGetValue(capability.ServerId, out var server))
            {
                continue;
            }

            capabilityOverrides[(server.Slug, capability.Kind, capability.Name)] = capability.Enabled;
        }

        return new ActiveProfileContext
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            EnabledServerSlugs = enabledServerSlugs,
            CapabilityOverrides = capabilityOverrides
        };
    }

    public async Task<List<ProfileSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
    {
        return await db.Profiles
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new ProfileSummary
            {
                Id = p.Id,
                Name = p.Name,
                IsDefault = p.IsDefault,
                IsActive = false,
                IncludedServerCount = p.ProfileServers.Count
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<ProfileEditModel> CreateDraftAsync(CancellationToken cancellationToken = default)
    {
        var servers = await db.Servers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

        var capabilities = await db.ServerCapabilities
            .AsNoTracking()
            .Where(c => c.Kind == ServerCapability.ToolKind)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        return new ProfileEditModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Servers =
            [
                .. servers.Select(server => new ProfileServerEdit
                {
                    ServerId = server.Id,
                    ServerSlug = server.Slug,
                    ServerName = server.Name,
                    ToolCapabilities =
                    [
                        .. capabilities
                            .Where(c => c.ServerId == server.Id)
                            .Select(c => new CapabilityEdit
                            {
                                Name = c.Name,
                                Enabled = true
                            })
                    ]
                })
            ]
        };
    }

    public async Task<ProfileEditModel?> GetForEditAsync(string id, CancellationToken cancellationToken = default)
    {
        var profile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var draft = await CreateDraftAsync(cancellationToken);
        draft.Id = profile.Id;
        draft.Name = profile.Name;
        draft.IsDefault = profile.IsDefault;

        var profileServers = await db.ProfileServers
            .AsNoTracking()
            .Where(ps => ps.ProfileId == id)
            .ToListAsync(cancellationToken);
        var profileServerLookup = profileServers.ToDictionary(ps => ps.ServerId, StringComparer.Ordinal);

        var toolOverrides = await db.ProfileCapabilities
            .AsNoTracking()
            .Where(pc => pc.ProfileId == id && pc.Kind == ServerCapability.ToolKind)
            .ToListAsync(cancellationToken);
        var toolOverrideLookup = toolOverrides.ToDictionary(pc => (pc.ServerId, pc.Name), pc => pc.Enabled);

        foreach (var server in draft.Servers)
        {
            if (profileServerLookup.TryGetValue(server.ServerId, out var profileServer))
            {
                server.Included = true;
                server.Enabled = profileServer.Enabled;
            }

            foreach (var capability in server.ToolCapabilities)
            {
                if (toolOverrideLookup.TryGetValue((server.ServerId, capability.Name), out var enabled))
                {
                    capability.Enabled = enabled;
                }
            }
        }

        return draft;
    }

    public async Task<string> SaveAsync(ProfileEditModel model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            throw new InvalidOperationException("Profile name is required.");
        }

        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == model.Id, cancellationToken);
        var isNew = profile is null;

        if (isNew)
        {
            profile = new Profile
            {
                Id = string.IsNullOrWhiteSpace(model.Id) ? Guid.NewGuid().ToString("N") : model.Id,
                CreatedAt = DateTime.UtcNow
            };

            db.Profiles.Add(profile);
        }

        profile!.Name = model.Name.Trim();
        profile.IsDefault = model.IsDefault;
        profile.UpdatedAt = DateTime.UtcNow;

        if (profile.IsDefault)
        {
            var otherDefaults = await db.Profiles
                .Where(p => p.Id != profile.Id && p.IsDefault)
                .ToListAsync(cancellationToken);

            foreach (var other in otherDefaults)
            {
                other.IsDefault = false;
                other.UpdatedAt = DateTime.UtcNow;
            }
        }

        var existingProfileServers = await db.ProfileServers
            .Where(ps => ps.ProfileId == profile.Id)
            .ToListAsync(cancellationToken);
        var existingToolCapabilities = await db.ProfileCapabilities
            .Where(pc => pc.ProfileId == profile.Id && pc.Kind == ServerCapability.ToolKind)
            .ToListAsync(cancellationToken);

        db.ProfileCapabilities.RemoveRange(existingToolCapabilities);
        db.ProfileServers.RemoveRange(existingProfileServers);

        foreach (var server in model.Servers.Where(s => s.Included))
        {
            db.ProfileServers.Add(new ProfileServer
            {
                ProfileId = profile.Id,
                ServerId = server.ServerId,
                Enabled = server.Enabled
            });

            foreach (var capability in server.ToolCapabilities.Where(c => !c.Enabled))
            {
                db.ProfileCapabilities.Add(new ProfileCapability
                {
                    ProfileId = profile.Id,
                    ServerId = server.ServerId,
                    Kind = ServerCapability.ToolKind,
                    Name = capability.Name,
                    Enabled = false
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return profile.Id;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (profile is null)
        {
            return false;
        }

        var activeSetting = await db.Settings.FirstOrDefaultAsync(s => s.Key == ActiveProfileIdSettingKey, cancellationToken);
        if (activeSetting?.Value == id)
        {
            db.Settings.Remove(activeSetting);
        }

        db.Profiles.Remove(profile);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
