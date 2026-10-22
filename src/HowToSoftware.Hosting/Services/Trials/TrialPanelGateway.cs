using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl.Models;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl.Requests;
using HowToSoftware.Hosting.Models;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Trials;

/// <summary>Connects verified, durable trial records to the operator's Application API.</summary>
public sealed class TrialPanelGateway(
    IPterodactylClient panel,
    IGameTemplateCatalog templates,
    IOptionsMonitor<TrialOptions> trials) : ITrialPanelGateway
{
    // Existing trials still need suspension, cleanup and paid conversion after an operator
    // disables new signups or removes an egg/profile from the public selection.
    public bool IsConfigured => panel.IsConfigured;

    public bool CanProvision(string gameSlug, string? profileId = null, string? version = null)
    {
        if (!panel.IsConfigured)
        {
            return false;
        }
        if (string.Equals(gameSlug, GameTemplateCatalog.ProjectZomboidId, StringComparison.Ordinal))
        {
            return IsTemplateConfigured(templates.Find(GameTemplateCatalog.ProjectZomboidId));
        }
        var matches = trials.CurrentValue.Profiles.Where(p =>
            string.Equals(p.GameSlug, gameSlug, StringComparison.Ordinal) &&
            string.Equals(p.Id, profileId, StringComparison.Ordinal)).ToArray();
        return string.Equals(gameSlug, "minecraft", StringComparison.Ordinal) &&
            matches is [var profile] && IsProfileConfigured(profile) && version is not null &&
            profile.AllowedVersions.Contains(version, StringComparer.Ordinal);
    }

    public async Task<TrialPanelUser> ResolveUserAsync(
        string email, string displayName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var canonicalEmail = email.Trim().ToLowerInvariant();
        if (!MailAddress.TryCreate(canonicalEmail, out var address) ||
            !string.Equals(address.Address, canonicalEmail, StringComparison.Ordinal))
        {
            throw new PterodactylApiException(PterodactylFailure.ValidationFailed, "The trial email is invalid.");
        }

        var externalId = "hts-trial-user:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalEmail)))[..32];
        var existing = await panel.FindUserByEmailAsync(canonicalEmail, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return DescribeUser(existing, canonicalEmail);
        }

        var parts = (displayName ?? string.Empty).Trim()
            .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var firstName = parts.Length > 0 ? parts[0] : "HTS";
        var lastName = parts.Length > 1 ? parts[1] : "Player";
        try
        {
            var created = await panel.CreateUserAsync(new CreateUserRequest
            {
                ExternalId = externalId,
                Email = canonicalEmail,
                Username = PterodactylNaming.BuildUsername(canonicalEmail.Split('@')[0]),
                FirstName = firstName[..Math.Min(firstName.Length, 64)],
                LastName = lastName[..Math.Min(lastName.Length, 64)],
                // Omitting password lets the panel send its own secure password-setup email.
                // Neither HTS nor its browser needs to handle a panel password.
                Password = null,
                RootAdmin = false
            }, cancellationToken).ConfigureAwait(false);
            return DescribeUser(created, canonicalEmail);
        }
        catch (PterodactylApiException exception)
            when (exception.StatusCode == 422 || exception.Failure is PterodactylFailure.AlreadyExists)
        {
            var raced = await panel.FindUserByEmailAsync(canonicalEmail, cancellationToken).ConfigureAwait(false);
            if (raced is null)
            {
                throw;
            }
            return DescribeUser(raced, canonicalEmail);
        }
    }

    public async Task<TrialPanelServer?> FindServerAsync(string externalId, CancellationToken cancellationToken)
    {
        EnsureTrialExternalId(externalId);
        var server = await panel.FindServerByExternalIdAsync(externalId, cancellationToken).ConfigureAwait(false);
        if (server is null)
        {
            return null;
        }
        if (!string.Equals(server.ExternalId, externalId, StringComparison.Ordinal))
        {
            throw Forbidden();
        }
        return DescribeServer(server);
    }

    public async Task<TrialPanelServer> CreateServerAsync(
        TrialProvisioningRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ExternalId, $"hts-trial-{request.TrialId:N}", StringComparison.Ordinal))
        {
            throw Forbidden();
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.UserId);
        ValidateLimits(request.Limits);

        var existing = await panel.FindServerByExternalIdAsync(request.ExternalId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureTrialServer(existing, request.ExternalId, request.UserId);
            return DescribeServer(existing);
        }

        var template = ResolveTemplate(request);
        var egg = await panel.GetEggAsync(template.NestId, template.EggId, cancellationToken).ConfigureAwait(false);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in egg.Variables)
        {
            environment[variable.EnvVariable] = variable.DefaultValue ?? string.Empty;
        }
        foreach (var (key, value) in template.Environment)
        {
            environment[key] = value;
        }
        TrialServerIdentity.SetProjectZomboidPassword(request.GameSlug, environment);
        var limits = request.Limits;
        var payload = new CreateServerRequest
        {
            ExternalId = request.ExternalId,
            Name = TrialServerIdentity.ServerName(request.DisplayName),
            User = request.UserId,
            Egg = template.EggId,
            DockerImage = template.DockerImage ?? egg.DockerImage,
            Startup = template.StartupCommand ?? egg.Startup,
            Environment = environment,
            Limits = new(limits.MemoryMb, 0, limits.DiskMb, 500, limits.CpuPercent),
            FeatureLimits = new(limits.Databases,
                request.GameSlug == GameTemplateCatalog.ProjectZomboidId ? Math.Max(2, limits.Allocations) : limits.Allocations,
                limits.Backups),
            // Public-node selection and capacity validation remain the panel's responsibility.
            Deploy = new([template.LocationId], false, template.PortRange),
            StartOnCompletion = true,
            Description = "HTS verified trial. Suspend after 24 hours; retain files for the configured conversion window."
        };

        try
        {
            var created = await panel.CreateServerAsync(payload, cancellationToken).ConfigureAwait(false);
            EnsureTrialServer(created, request.ExternalId, request.UserId);
            return DescribeServer(created);
        }
        catch (PterodactylApiException exception) when (exception.Failure is PterodactylFailure.AlreadyExists)
        {
            var raced = await panel.FindServerByExternalIdAsync(request.ExternalId, cancellationToken).ConfigureAwait(false);
            if (raced is null)
            {
                throw;
            }
            EnsureTrialServer(raced, request.ExternalId, request.UserId);
            return DescribeServer(raced);
        }
    }

    public async Task<TrialPanelServer?> InspectServerAsync(int serverId, CancellationToken cancellationToken)
    {
        var server = await ReadTrialServerAsync(serverId, cancellationToken).ConfigureAwait(false);
        return server is null ? null : DescribeServer(server);
    }

    public async Task SuspendAsync(int serverId, CancellationToken cancellationToken)
    {
        var server = await ReadTrialServerAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (server is null || IsSuspended(server))
        {
            return;
        }
        await panel.SuspendServerAsync(serverId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int serverId, CancellationToken cancellationToken)
    {
        if (await ReadTrialServerAsync(serverId, cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }
        try
        {
            await panel.DeleteServerAsync(serverId, force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (PterodactylApiException exception) when (exception.Failure is PterodactylFailure.NotFound)
        {
            // A retried cleanup may race another completed cleanup of this same trial.
        }
    }

    public async Task UpgradeAsync(
        int serverId, TrialResourceLimits limits, CancellationToken cancellationToken)
    {
        ValidateLimits(limits);
        var server = await ReadTrialServerAsync(serverId, cancellationToken).ConfigureAwait(false)
            ?? throw new PterodactylApiException(PterodactylFailure.NotFound, "The trial server no longer exists.");
        var previous = server.Limits ?? throw new PterodactylApiException(
            PterodactylFailure.PanelError, "The panel omitted the existing server limits.");
        if (server.Allocation <= 0)
        {
            throw new PterodactylApiException(PterodactylFailure.PanelError,
                "The panel omitted the existing server allocation.");
        }

        // Bound ports remain attached. If allocation relationships are unavailable to this
        // key, retaining the previous cap is safer than setting it below existing bindings.
        var boundAllocations = server.Relationships?.Allocations?.Items.Count
            ?? server.FeatureLimits?.Allocations ?? 1;
        var updated = await panel.UpdateServerBuildAsync(serverId, new UpdateServerBuildRequest
        {
            Allocation = server.Allocation,
            Limits = new(limits.MemoryMb, previous.Swap, limits.DiskMb, previous.Io, limits.CpuPercent)
            {
                Threads = previous.Threads
            },
            FeatureLimits = new(limits.Databases, Math.Max(limits.Allocations, Math.Max(1, boundAllocations)), limits.Backups),
            OomDisabled = previous.OomDisabled
        }, cancellationToken).ConfigureAwait(false);
        EnsureTrialServer(updated, server.ExternalId!, server.User);
        if (updated.Id != server.Id || updated.Allocation != server.Allocation ||
            updated.Node != server.Node || updated.Egg != server.Egg)
        {
            throw new PterodactylApiException(PterodactylFailure.PanelError,
                "The panel changed server identity during the resource update.");
        }
    }

    public async Task ResumeAsync(int serverId, CancellationToken cancellationToken)
    {
        var server = await ReadTrialServerAsync(serverId, cancellationToken).ConfigureAwait(false)
            ?? throw new PterodactylApiException(PterodactylFailure.NotFound, "The trial server no longer exists.");
        if (IsSuspended(server))
        {
            await panel.UnsuspendServerAsync(serverId, cancellationToken).ConfigureAwait(false);
        }
    }

    private GameTemplate ResolveTemplate(TrialProvisioningRequest request)
    {
        if (string.Equals(request.GameSlug, GameTemplateCatalog.ProjectZomboidId, StringComparison.Ordinal))
        {
            var template = templates.Find(GameTemplateCatalog.ProjectZomboidId);
            if (!IsTemplateConfigured(template))
            {
                throw new PterodactylApiException(PterodactylFailure.NotConfigured,
                    "The trial game template is not configured.");
            }
            return template!;
        }
        if (!string.Equals(request.GameSlug, "minecraft", StringComparison.Ordinal))
        {
            throw new PterodactylApiException(PterodactylFailure.ValidationFailed, "The trial game is not supported.");
        }

        if (!CanProvision(request.GameSlug, request.ProfileId, request.Version))
        {
            throw new PterodactylApiException(PterodactylFailure.ValidationFailed,
                "The selected trial profile or version is not approved.");
        }
        var profile = trials.CurrentValue.Profiles.SingleOrDefault(p => p.Enabled &&
            string.Equals(p.GameSlug, request.GameSlug, StringComparison.Ordinal) &&
            string.Equals(p.Id, request.ProfileId, StringComparison.Ordinal));
        if (profile is null ||
            string.IsNullOrWhiteSpace(request.Version) ||
            !profile.AllowedVersions.Contains(request.Version, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(profile.VersionEnvironmentVariable))
        {
            throw new PterodactylApiException(PterodactylFailure.ValidationFailed,
                "The selected trial profile or version is not approved.");
        }
        var environment = new Dictionary<string, string>(profile.Environment, StringComparer.Ordinal)
        {
            [profile.VersionEnvironmentVariable] = request.Version
        };
        return new GameTemplate
        {
            Id = profile.Id,
            GameName = $"Minecraft {profile.Edition} {profile.Variant}",
            NestId = profile.NestId,
            EggId = profile.EggId,
            LocationId = profile.LocationId,
            DockerImage = string.IsNullOrWhiteSpace(profile.DockerImage) ? null : profile.DockerImage,
            StartupCommand = string.IsNullOrWhiteSpace(profile.StartupCommand) ? null : profile.StartupCommand,
            Environment = environment,
            PortRange = profile.PortRange
        };
    }

    private async Task<PterodactylServer?> ReadTrialServerAsync(int serverId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serverId);
        var server = await panel.GetServerAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (server is not null)
        {
            if (server.Id != serverId)
            {
                throw Forbidden();
            }
            EnsureTrialExternalId(server.ExternalId);
        }
        return server;
    }

    private static bool IsTemplateConfigured(GameTemplate? template) =>
        template is { NestId: > 0, EggId: > 0, LocationId: > 0 };

    private static bool IsProfileConfigured(TrialGameProfile profile) =>
        profile.Enabled && profile.GameSlug == "minecraft" && profile.Edition is "java" or "bedrock" &&
        !string.IsNullOrWhiteSpace(profile.Id) && !string.IsNullOrWhiteSpace(profile.Variant) &&
        profile.NestId > 0 && profile.EggId > 0 && profile.LocationId > 0 &&
        profile.AllowedVersions.Count > 0 && !string.IsNullOrWhiteSpace(profile.VersionEnvironmentVariable);

    private static TrialPanelUser DescribeUser(PterodactylUser user, string canonicalEmail)
    {
        if (user.Id <= 0 || user.RootAdmin ||
            !string.Equals(user.Email.Trim(), canonicalEmail, StringComparison.OrdinalIgnoreCase))
        {
            throw Forbidden();
        }
        return new(user.Id, user.Email);
    }

    private static void EnsureTrialServer(PterodactylServer server, string externalId, int userId)
    {
        if (server.Id <= 0 || !string.Equals(server.ExternalId, externalId, StringComparison.Ordinal) ||
            server.User != userId)
        {
            throw Forbidden();
        }
    }

    private static void EnsureTrialExternalId(string? externalId)
    {
        const string prefix = "hts-trial-";
        if (externalId is null || !externalId.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(externalId[prefix.Length..], "N", out _))
        {
            throw Forbidden();
        }
    }

    private static bool IsSuspended(PterodactylServer server) =>
        server.Suspended || string.Equals(server.Status, "suspended", StringComparison.Ordinal);

    private static TrialPanelServer DescribeServer(PterodactylServer server) => new(
        server.Id, server.Identifier, server.Uuid,
        server.Container?.Installed == 1 || server.Status is null or "suspended",
        IsSuspended(server), server.Name,
        server.Limits is { } limits ? new((int)limits.Memory, limits.Cpu, (int)limits.Disk) : null,
        server.Relationships?.Allocations?.Items.FirstOrDefault(x => x.Id == server.Allocation)?.Ip,
        server.Relationships?.Allocations?.Items.FirstOrDefault(x => x.Id == server.Allocation)?.Port,
        server.User, server.ExternalId);

    private static void ValidateLimits(TrialResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MemoryMb < 0 || limits.DiskMb < 0 || limits.CpuPercent < 0 ||
            limits.Backups < 0 || limits.Databases < 0 || limits.Allocations < 1)
        {
            throw new PterodactylApiException(PterodactylFailure.ValidationFailed, "The trial resource limits are invalid.");
        }
    }

    private static PterodactylApiException Forbidden() =>
        new(PterodactylFailure.Forbidden, "The panel resource is not an HTS customer trial.");
}
