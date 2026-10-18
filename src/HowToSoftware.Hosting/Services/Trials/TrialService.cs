using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Trials;

/// <summary>Verified, one-per-account trials with durable, retry-safe lifecycle transitions.</summary>
public sealed class TrialService(
    ITrialStore store,
    ITrialPanelGateway panel,
    ITrialEmailSender email,
    IOptionsMonitor<TrialOptions> settings,
    IOptions<SiteOptions> site,
    IOptionsMonitor<PterodactylOptions> panelSettings,
    TimeProvider clock,
    ILogger<TrialService> logger) : ITrialService
{
    public bool IsAvailable => settings.CurrentValue.Enabled && store.IsConfigured && panel.IsConfigured && email.IsConfigured;
    public IReadOnlyList<TrialGameProfile> AvailableProfiles => settings.CurrentValue.Profiles
        .Where(x => IsApprovedProfile(x) && panel.CanProvision(x.GameSlug, x.Id,
            x.GameSlug == "minecraft" ? x.AllowedVersions[0] : null))
        .Select(x => new TrialGameProfile { Id = x.Id, GameSlug = x.GameSlug, Edition = x.Edition, Variant = x.Variant,
            Enabled = true, AllowedVersions = [.. x.AllowedVersions] }).ToArray();

    public async Task<TrialRequestResult> RequestAsync(TrialSelectionRequest request, CancellationToken ct = default)
    {
        if (!store.IsConfigured || !email.IsConfigured) return new(TrialRequestOutcome.Unavailable);
        var normalized = NormalizeEmail(request.Email);
        var displayName = TrialServerIdentity.NormalizeDisplayName(request.DisplayName);
        if (normalized is null || displayName is null)
            return new(TrialRequestOutcome.InvalidSelection);

        try
        {
            var now = clock.GetUtcNow();
            var existing = await store.FindByEmailAsync(normalized, ct);
            // Recovery of an existing owner's access remains possible after new trials are disabled.
            if (existing?.VerifiedAt is null && !IsAvailable) return new(TrialRequestOutcome.Unavailable);
            if (existing?.VerifiedAt is null && !IsSelectionAllowed(request)) return new(TrialRequestOutcome.InvalidSelection);
            if (existing?.State == TrialState.Rejected) return new(TrialRequestOutcome.Sent);
            if (existing?.VerificationTokenHash is not null
                && existing.VerificationExpiresAt > now.AddMinutes(settings.CurrentValue.VerificationLifetimeMinutes - 2))
                return new(TrialRequestOutcome.Sent);

            var token = CreateToken();
            var record = existing ?? new ServerTrialRecord
            {
                Id = Guid.NewGuid(), NormalizedEmail = normalized, DisplayName = displayName,
                GameSlug = request.GameSlug, ProfileId = request.ProfileId, Version = request.Version,
                State = TrialState.PendingVerification, CreatedAt = now, UpdatedAt = now
            };
            record.VerificationTokenHash = HashToken(token);
            if (record.VerifiedAt is null)
            {
                record.GameSlug = request.GameSlug;
                record.ProfileId = request.ProfileId;
                record.Version = request.Version;
            }
            record.VerificationExpiresAt = now.AddMinutes(settings.CurrentValue.VerificationLifetimeMinutes);
            record.Locale = request.Locale == "pt-BR" ? "pt-BR" : "en";
            var stored = existing is null ? await store.AddPendingAsync(record, ct) : await store.RefreshVerificationAsync(record, now, ct);
            if (!stored) return new(TrialRequestOutcome.Sent);
            var url = site.Value.BaseUrl.TrimEnd('/') + "/trial?verify=" + Uri.EscapeDataString(token);
            await email.SendVerificationAsync(record.NormalizedEmail, record.DisplayName, url, record.Locale, ct);
            return new(TrialRequestOutcome.Sent);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogFailure("Trial verification request", error);
            return new(TrialRequestOutcome.Unavailable);
        }
    }

    public async Task<TrialConfirmationResult> ConfirmAsync(string verificationToken, CancellationToken ct = default)
    {
        if (!store.IsConfigured || !email.IsConfigured || !IsToken(verificationToken))
            return new(TrialConfirmationOutcome.InvalidOrExpired);
        try
        {
            var hash = HashToken(verificationToken);
            var record = await store.FindByVerificationAsync(hash, ct);
            var now = clock.GetUtcNow();
            if (record is null || record.VerificationExpiresAt <= now) return new(TrialConfirmationOutcome.InvalidOrExpired);
            if (record.VerifiedAt is null && !IsAvailable) return new(TrialConfirmationOutcome.Unavailable);
            var access = CreateToken();
            if (!await store.ConfirmAsync(record.Id, hash, HashToken(access), now, ct))
                return new(TrialConfirmationOutcome.InvalidOrExpired);
            return new(TrialConfirmationOutcome.Confirmed, access);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogFailure("Trial confirmation", error);
            return new(TrialConfirmationOutcome.Unavailable);
        }
    }

    public async Task<TrialRequestResult> RecoverAccessAsync(string address, string locale, CancellationToken ct = default)
    {
        if (!store.IsConfigured || !email.IsConfigured) return new(TrialRequestOutcome.Unavailable);
        var normalized = NormalizeEmail(address);
        if (normalized is null) return new(TrialRequestOutcome.InvalidSelection);
        try
        {
            var existing = await store.FindByEmailAsync(normalized, ct);
            if (existing?.VerifiedAt is null) return new(TrialRequestOutcome.Sent);
            return await RequestAsync(new(existing.NormalizedEmail, existing.DisplayName, existing.GameSlug,
                existing.ProfileId, existing.Version, locale), ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogFailure("Trial owner access recovery", error);
            return new(TrialRequestOutcome.Unavailable);
        }
    }

    public async Task<TrialAccountView?> GetByAccessTokenAsync(string accessToken, CancellationToken ct = default)
    {
        if (!store.IsConfigured || !IsToken(accessToken)) return null;
        try
        {
            var record = await store.FindByAccessAsync(HashToken(accessToken), ct);
            if (record is null) return null;
            TrialPanelServer? details = null;
            if (record.PterodactylServerId is { } serverId && panel.IsConfigured
                && record.State is TrialState.Active or TrialState.Converted or TrialState.Installing)
            {
                try
                {
                    var actual = await panel.InspectServerAsync(serverId, ct);
                    if (actual is not null && actual.UserId == record.PterodactylUserId && actual.ExternalId == ExternalId(record.Id))
                        details = actual;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    LogFailure("Trial live details", error);
                }
            }
            return View(record) with { Server = details };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogFailure("Trial owner lookup", error);
            return null;
        }
    }

    public async Task<TrialUpgradeReservation> ReserveUpgradeAsync(Guid trialId, string accessToken, Guid orderId, CancellationToken ct = default)
    {
        if (!store.IsConfigured || !IsToken(accessToken)) return new(false);
        var now = clock.GetUtcNow();
        return new(await store.ReserveUpgradeAsync(trialId, HashToken(accessToken), orderId, now,
            now.AddMinutes(settings.CurrentValue.ReservationLifetimeMinutes), ct), trialId);
    }

    public Task ReleaseUpgradeAsync(Guid orderId, CancellationToken ct = default) => store.ReleaseUpgradeAsync(orderId, ct);

    public async Task<TrialConversionResult> ConvertPaidOrderAsync(Guid orderId, TrialResourceLimits limits, CancellationToken ct = default)
    {
        try { return await ConvertCoreAsync(orderId, limits, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogFailure("Trial conversion lookup", error);
            return new(TrialConversionOutcome.Unavailable);
        }
    }

    private async Task<TrialConversionResult> ConvertCoreAsync(Guid orderId, TrialResourceLimits limits, CancellationToken ct)
    {
        var record = await store.FindByOrderAsync(orderId, ct);
        if (record is null) return new(TrialConversionOutcome.NotLinked);
        if (record.State == TrialState.Converted)
            return record.UpgradeOrderId == orderId
                ? new(TrialConversionOutcome.Converted, StoredServer(record))
                : new(TrialConversionOutcome.Retry);
        if (!panel.IsConfigured) return new(TrialConversionOutcome.Unavailable);
        if (!await store.IsPaidOrderAsync(orderId, ct)) return new(TrialConversionOutcome.Retry);
        var claimed = await store.ClaimAsync(record.Id, clock.GetUtcNow(), LeaseDuration, ct);
        if (claimed is null) return new(TrialConversionOutcome.Retry);
        var token = claimed.LeaseToken!.Value;
        try
        {
            if (claimed.PterodactylServerId is null
                || claimed.State is TrialState.Deleting or TrialState.Deleted or TrialState.Rejected)
                return new(TrialConversionOutcome.Retry);
            if (claimed.UpgradeOrderId is { } otherOrder && otherOrder != orderId && await store.IsPaidOrderAsync(otherOrder, ct))
                return new(TrialConversionOutcome.Retry);
            claimed.UpgradeOrderId = orderId;
            await SaveOrThrowAsync(claimed, ct);
            await RenewAsync(claimed, ct);
            var actual = await panel.InspectServerAsync(claimed.PterodactylServerId.Value, ct);
            if (actual is null || !actual.Installed) return new(TrialConversionOutcome.Retry);
            await RenewAsync(claimed, ct);
            await panel.UpgradeAsync(actual.Id, limits, ct);
            await RenewAsync(claimed, ct);
            if (actual.Suspended || claimed.SuspendedAt is not null) await panel.ResumeAsync(actual.Id, ct);
            claimed.State = TrialState.Converted;
            claimed.ConvertedAt = clock.GetUtcNow();
            claimed.ReservationExpiresAt = null;
            claimed.NextAttemptAt = null;
            if (!await store.SaveLeasedAsync(claimed, token, clock.GetUtcNow(), ct))
                return new(TrialConversionOutcome.Retry);
            return new(TrialConversionOutcome.Converted, actual with { Suspended = false });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogFailure("Trial conversion", error);
            return new(TrialConversionOutcome.Retry);
        }
        finally { await store.ReleaseLeaseAsync(claimed.Id, token, CancellationToken.None); }
    }

    public async Task ProcessDueAsync(CancellationToken ct = default)
    {
        // Enabled governs new requests only. Existing clocks and deletion obligations survive a toggle.
        if (!store.IsConfigured || !panel.IsConfigured) return;
        var ids = await store.ListDueAsync(clock.GetUtcNow(), ct);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var record = await store.ClaimAsync(id, clock.GetUtcNow(), LeaseDuration, ct);
            if (record is null) continue;
            var token = record.LeaseToken!.Value;
            try
            {
                await ProcessClaimedAsync(record, ct);
                await store.SaveLeasedAsync(record, token, clock.GetUtcNow(), ct);
            }
            catch (TrialAccountAlreadyClaimedException)
            {
                // A second verified email resolving to an already claimed panel account receives no second server.
                record.PterodactylUserId = null;
                record.State = TrialState.Rejected;
                record.LastFailureClass = "PanelAccountAlreadyClaimed";
                await store.SaveLeasedAsync(record, token, clock.GetUtcNow(), ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                record.LastFailureClass = error.GetType().Name;
                record.NextAttemptAt = clock.GetUtcNow().AddSeconds(settings.CurrentValue.RetryDelaySeconds);
                LogFailure("Trial lifecycle", error);
                // If an API operation succeeded but its response was lost, persisted IDs and external IDs recover it.
                await store.SaveLeasedAsync(record, token, clock.GetUtcNow(), ct);
            }
            finally { await store.ReleaseLeaseAsync(id, token, CancellationToken.None); }
        }
    }

    private async Task ProcessClaimedAsync(ServerTrialRecord record, CancellationToken ct)
    {
        record.NextAttemptAt = null;
        if (await store.FindPaidUpgradeAsync(record.Id, ct) is not null)
        {
            record.NextAttemptAt = clock.GetUtcNow().AddSeconds(settings.CurrentValue.RetryDelaySeconds);
            return;
        }
        if (record.UpgradeOrderId is { } order)
        {
            if (await store.IsPaidOrderAsync(order, ct)) return;
            if (await store.ClearUnpaidReservationAsync(record.Id, record.LeaseToken!.Value, clock.GetUtcNow(), ct))
            {
                record.UpgradeOrderId = null;
                record.ReservationExpiresAt = null;
            }
        }
        if (record.State is TrialState.Provisioning or TrialState.Installing)
        {
            if (record.VerifiedAt is null) throw new InvalidOperationException("Trial owner must be verified.");
            if (record.PterodactylUserId is null)
            {
                var user = await panel.ResolveUserAsync(record.NormalizedEmail, record.DisplayName, ct);
                record.PterodactylUserId = user.Id;
                await SaveOrThrowAsync(record, ct); // Unique panel account claim precedes server creation.
            }
            await RenewAsync(record, ct);
            var server = record.PterodactylServerId is { } serverId
                ? await panel.InspectServerAsync(serverId, ct)
                : await panel.FindServerAsync(ExternalId(record.Id), ct);
            if (server is null)
            {
                if (record.PterodactylServerId is not null)
                    throw new InvalidOperationException("Previously provisioned trial server is missing.");
                var profile = ResolveProfile(record.GameSlug, record.ProfileId, record.Version);
                await RenewAsync(record, ct);
                server = await panel.CreateServerAsync(new(record.Id, ExternalId(record.Id), record.PterodactylUserId.Value,
                    settings.CurrentValue.Limits, record.GameSlug, record.ProfileId, record.Version, profile, record.DisplayName), ct);
            }
            record.PterodactylServerId = server.Id;
            record.ServerIdentifier = server.Identifier;
            record.ServerUuid = server.Uuid;
            record.State = server.Installed ? TrialState.Active : TrialState.Installing;
            if (server.Installed && record.StartedAt is null)
            {
                record.StartedAt = clock.GetUtcNow();
                record.ExpiresAt = record.StartedAt.Value.AddHours(settings.CurrentValue.DurationHours);
                record.DeleteAfter = record.ExpiresAt.Value.AddHours(settings.CurrentValue.RetentionHours);
            }
            record.NextAttemptAt = server.Installed ? null : clock.GetUtcNow().AddSeconds(settings.CurrentValue.PollIntervalSeconds);
            await SaveOrThrowAsync(record, ct);
        }
        if (record.State == TrialState.Active && record.ExpiresAt <= clock.GetUtcNow())
        {
            await RenewAsync(record, ct);
            await panel.SuspendAsync(record.PterodactylServerId!.Value, ct);
            record.State = TrialState.Expired;
            record.SuspendedAt = clock.GetUtcNow();
            record.DeleteAfter ??= record.ExpiresAt!.Value.AddHours(settings.CurrentValue.RetentionHours);
            await SaveOrThrowAsync(record, ct);
        }
        if (record.State == TrialState.Expired && record.DeleteAfter <= clock.GetUtcNow())
        {
            // Entering Deleting atomically excludes new reservations; paid or unresolved reservations block it.
            if (await store.BeginDeleteAsync(record.Id, record.LeaseToken!.Value, clock.GetUtcNow(), ct))
                record.State = TrialState.Deleting;
        }
        if (record.State == TrialState.Deleting)
        {
            await RenewAsync(record, ct);
            await panel.DeleteAsync(record.PterodactylServerId!.Value, ct);
            record.State = TrialState.Deleted;
            record.DeletedAt = clock.GetUtcNow();
            await SaveOrThrowAsync(record, ct);
        }
        if (!email.IsConfigured) return;
        // Send at-least-once after durable state. A crash can duplicate a notification, never restart a trial.
        if (record.State == TrialState.Active && record.ReadyEmailSentAt is null)
        {
            await email.SendReadyAsync(record.NormalizedEmail, panelSettings.CurrentValue.NormalisedBaseUrl,
                record.ExpiresAt!.Value, record.Locale, ct);
            record.ReadyEmailSentAt = clock.GetUtcNow();
        }
        if (record.State == TrialState.Expired && record.ExpiredEmailSentAt is null)
        {
            await email.SendExpiredAsync(record.NormalizedEmail, record.DeleteAfter!.Value, record.Locale, ct);
            record.ExpiredEmailSentAt = clock.GetUtcNow();
        }
        if (record.State == TrialState.Deleted && record.DeletedEmailSentAt is null)
        {
            await email.SendDeletedAsync(record.NormalizedEmail, record.Locale, ct);
            record.DeletedEmailSentAt = clock.GetUtcNow();
        }
        record.LastFailureClass = null;
    }

    private TimeSpan LeaseDuration => TimeSpan.FromMinutes(settings.CurrentValue.LeaseMinutes);
    private async Task RenewAsync(ServerTrialRecord record, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!await store.RenewAsync(record.Id, record.LeaseToken!.Value, now, LeaseDuration, ct))
            throw new InvalidOperationException("Trial lifecycle lease was lost.");
        record.LeaseExpiresAt = now + LeaseDuration;
    }
    private async Task SaveOrThrowAsync(ServerTrialRecord record, CancellationToken ct)
    {
        if (!await store.SaveLeasedAsync(record, record.LeaseToken!.Value, clock.GetUtcNow(), ct))
            throw new InvalidOperationException("Trial lifecycle lease was lost.");
    }
    private TrialAccountView View(ServerTrialRecord x) => new(x.Id, x.NormalizedEmail, x.DisplayName, x.GameSlug,
        x.ProfileId, x.Version, x.State, x.PterodactylServerId, x.ServerIdentifier, x.StartedAt, x.ExpiresAt,
        x.DeleteAfter, x.PterodactylServerId is not null && x.UpgradeOrderId is null
            && (x.State is TrialState.Active or TrialState.Expired)
            && (x.DeleteAfter is null || x.DeleteAfter > clock.GetUtcNow())
            && (x.LeaseToken is null || x.LeaseExpiresAt <= clock.GetUtcNow()), x.UpgradeOrderId);

    private bool IsSelectionAllowed(TrialSelectionRequest request)
    {
        if (request.GameSlug is not ("project-zomboid" or "minecraft")) return false;
        if (!panel.CanProvision(request.GameSlug, request.ProfileId, request.Version)) return false;
        if (request.GameSlug == "project-zomboid" && request.ProfileId is null) return request.Version is null;
        return ResolveProfile(request.GameSlug, request.ProfileId, request.Version) is not null;
    }
    private TrialGameProfile? ResolveProfile(string game, string? id, string? version) =>
        settings.CurrentValue.Profiles.SingleOrDefault(x => IsApprovedProfile(x) && x.GameSlug == game && x.Id == id
            && (game != "minecraft" || (version is not null && x.AllowedVersions.Contains(version, StringComparer.Ordinal))));

    private static bool IsApprovedProfile(TrialGameProfile x) => x.Enabled && !string.IsNullOrWhiteSpace(x.Id)
        && x.Id.Length <= 64 && x.NestId > 0 && x.EggId > 0
        && x.GameSlug is "minecraft" or "project-zomboid"
        && (x.GameSlug != "minecraft" || (x.Edition is "java" or "bedrock"
            && !(x.Edition == "bedrock" && x.Variant is "forge" or "fabric")
            && x.AllowedVersions.Count > 0 && !string.IsNullOrWhiteSpace(x.VersionEnvironmentVariable)));
    private static TrialPanelServer StoredServer(ServerTrialRecord x) =>
        new(x.PterodactylServerId!.Value, x.ServerIdentifier!, x.ServerUuid!, true, false);
    public static string ExternalId(Guid id) => $"hts-trial-{id:N}";
    public static string? NormalizeEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320 || value.Any(char.IsControl)) return null;
        var trimmed = value.Trim();
        return MailAddress.TryCreate(trimmed, out var parsed) && string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase)
            ? trimmed.ToLowerInvariant() : null;
    }
    private static bool IsToken(string token) => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private void LogFailure(string operation, Exception error) =>
        logger.LogWarning("{Operation} failed ({FailureClass}); credentials and email are omitted.", operation, error.GetType().Name);
}
