using System.Data;
using System.Globalization;
using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Identity;

/// <summary>Existing HTS identity remains authoritative; email/name claims never merge accounts.</summary>
public sealed class ExternalIdentityService(IDbContextFactory<CommerceDbContext> factory,
    IOptions<ExternalIdentityOptions> settings, ICustomerAccess access, TimeProvider clock) : IExternalIdentityService
{
    private bool AnyEnabled => settings.Value.ActiveDirectory.Enabled || settings.Value.Discord.Enabled;
    private bool Allowed(string provider, string purpose) => access.Enabled && (purpose == "link"
        ? settings.Value.LinkingEnabled(provider) : purpose == "login" && settings.Value.LoginEnabled(provider));

    public async Task<IReadOnlyList<ExternalConnection>> ConnectionsAsync(string? ownerSession, CancellationToken ct)
    {
        if (!AnyEnabled || !access.Enabled) return [];
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = await OwnerAsync(db, ownerSession, ct);
        return owner is null ? [] : await db.CustomerExternalIdentities.AsNoTracking()
            .Where(x => x.CustomerAccessId == owner.Id).OrderBy(x => x.Provider)
            .Select(x => new ExternalConnection(x.Provider, x.DisplayName, x.LinkedAt)).ToListAsync(ct);
    }

    public async Task<ExternalFlowStart?> BeginAsync(string provider, string purpose, string? ownerSession, CancellationToken ct)
    {
        if (!Allowed(provider, purpose)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = purpose == "link" ? await OwnerAsync(db, ownerSession, ct) : null;
        if (purpose == "link" && owner is null) return null;
        var secret = CustomerAccessService.NewToken();
        var now = clock.GetUtcNow();
        var flow = new ExternalIdentityFlow { Id = Guid.NewGuid(), Provider = provider, Purpose = purpose,
            CustomerAccessId = owner?.Id, OwnerSessionHash = owner?.SessionHash,
            BrowserSecretHash = CustomerAccessService.Hash(secret), CreatedAt = now, ExpiresAt = now.AddMinutes(10) };
        db.ExternalIdentityFlows.Add(flow);
        await db.SaveChangesAsync(ct);
        return new(flow.Id, secret);
    }

    // Called only after the protocol handler has validated state/correlation and provider identity.
    // No public route accepts an issuer, subject, email or Discord ID from the frontend.
    internal async Task<bool> VerifyAsync(Guid id, string provider, string issuer, string subject, string? displayName, CancellationToken ct)
    {
        if (!ValidProof(provider, issuer, subject)) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var name = CleanDisplayName(displayName, provider);
        return await db.ExternalIdentityFlows.Where(x => x.Id == id && x.Provider == provider && x.VerifiedAt == null
                && x.ConsumedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Issuer, issuer).SetProperty(x => x.Subject, subject)
                .SetProperty(x => x.DisplayName, name).SetProperty(x => x.VerifiedAt, now), ct) == 1;
    }

    public async Task<ExternalFlowView?> PendingAsync(Guid id, string? secret, string? session, CancellationToken ct)
    {
        if (!AnyEnabled || !access.Enabled || !CustomerAccessService.ValidToken(secret)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var flow = await ReadFlowAsync(db, id, secret!, session, ct);
        return flow is null ? null : new(flow.Id, flow.Provider, flow.Purpose, flow.DisplayName!);
    }

    public async Task<ExternalFlowCompletion> CompleteAsync(Guid id, string? secret, string? session, CancellationToken ct)
    {
        if (!AnyEnabled || !access.Enabled || !CustomerAccessService.ValidToken(secret)) return new(false);
        await using var strategyDb = await factory.CreateDbContextAsync(ct);
        return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var flow = await ReadFlowAsync(db, id, secret!, session, ct);
            if (flow is null) return new ExternalFlowCompletion(false);
            var now = clock.GetUtcNow();
            if (await db.ExternalIdentityFlows.Where(x => x.Id == flow.Id && x.ConsumedAt == null && x.ExpiresAt > now)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ConsumedAt, now), ct) != 1) return new(false);

            string? token = null;
            if (flow.Purpose == "link")
            {
                var conflicts = await db.CustomerExternalIdentities.Where(x => x.Provider == flow.Provider
                    && (x.CustomerAccessId == flow.CustomerAccessId || (x.Issuer == flow.Issuer && x.Subject == flow.Subject))).ToListAsync(ct);
                if (conflicts.Count != 0 && !conflicts.All(x => x.CustomerAccessId == flow.CustomerAccessId
                    && x.Issuer == flow.Issuer && x.Subject == flow.Subject)) return new(false);
                if (conflicts.Count == 0)
                    db.CustomerExternalIdentities.Add(new() { Id = Guid.NewGuid(), CustomerAccessId = flow.CustomerAccessId!.Value,
                        Provider = flow.Provider, Issuer = flow.Issuer!, Subject = flow.Subject!,
                        DisplayName = flow.DisplayName!, LinkedAt = now });
            }
            else
            {
                var linkedOwner = await (from link in db.CustomerExternalIdentities
                    join owner in db.CustomerAccess on link.CustomerAccessId equals owner.Id
                    where link.Provider == flow.Provider && link.Issuer == flow.Issuer && link.Subject == flow.Subject
                    select owner.Id).SingleOrDefaultAsync(ct);
                if (linkedOwner == Guid.Empty) return new(false); // Explicit link first; no auto-provision/merge.
                token = CustomerAccessService.NewToken();
                await db.CustomerAccess.Where(x => x.Id == linkedOwner).ExecuteUpdateAsync(u =>
                    u.SetProperty(x => x.SessionHash, CustomerAccessService.Hash(token))
                        .SetProperty(x => x.UsedAt, now).SetProperty(x => x.SessionExpiresAt, now.AddMinutes(30)), ct);
            }
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return new(false); } // Unique indexes reject concurrent/foreign links.
            await transaction.CommitAsync(ct);
            return new(true, token);
        });
    }

    public async Task<bool> UnlinkAsync(string provider, string? session, CancellationToken ct)
    {
        if (!AnyEnabled || !access.Enabled || !ExternalProviders.Known(provider)) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = await OwnerAsync(db, session, ct);
        if (owner is null) return false;
        // Remove only this customer's binding. Existing bounded HTS sessions stay valid;
        // the removed identity cannot be used for another login. Email recovery is unchanged.
        await db.CustomerExternalIdentities.Where(x => x.CustomerAccessId == owner.Id && x.Provider == provider).ExecuteDeleteAsync(ct);
        return true;
    }

    private async Task<ExternalIdentityFlow?> ReadFlowAsync(CommerceDbContext db, Guid id, string secret, string? session, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var hash = CustomerAccessService.Hash(secret);
        var flow = await db.ExternalIdentityFlows.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.BrowserSecretHash == hash
            && x.VerifiedAt != null && x.ConsumedAt == null && x.ExpiresAt > now, ct);
        if (flow is null || !Allowed(flow.Provider, flow.Purpose) || !ValidProof(flow.Provider, flow.Issuer!, flow.Subject!)) return null;
        if (flow.Purpose == "link")
        {
            var owner = await OwnerAsync(db, session, ct);
            if (owner is null || owner.Id != flow.CustomerAccessId || owner.SessionHash != flow.OwnerSessionHash) return null;
        }
        return flow;
    }
    private async Task<CustomerAccessRecord?> OwnerAsync(CommerceDbContext db, string? session, CancellationToken ct)
    {
        if (!CustomerAccessService.ValidToken(session)) return null;
        var hash = CustomerAccessService.Hash(session!); var now = clock.GetUtcNow();
        return await db.CustomerAccess.AsNoTracking().SingleOrDefaultAsync(x => x.SessionHash == hash && x.SessionExpiresAt > now, ct);
    }
    private bool ValidProof(string provider, string issuer, string subject)
    {
        if (!settings.Value.LinkingEnabled(provider) || string.IsNullOrEmpty(issuer) || issuer.Length > 256
            || string.IsNullOrEmpty(subject) || subject.Length > 128 || subject.Any(char.IsControl)) return false;
        if (provider == ExternalProviders.Discord) return issuer == ExternalProviders.DiscordIssuer
            && ExternalIdentityRegistration.Snowflake(subject);
        return provider == ExternalProviders.Directory
            && string.Equals(issuer, settings.Value.ActiveDirectory.Issuer, StringComparison.Ordinal);
    }
    private static string CleanDisplayName(string? name, string provider)
    {
        var safe = new string((name ?? provider).Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safe)) return provider;
        var result = new System.Text.StringBuilder();
        foreach (var rune in safe.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > 128) break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}

public sealed class UnavailableExternalIdentityService : IExternalIdentityService
{
    public Task<IReadOnlyList<ExternalConnection>> ConnectionsAsync(string? session, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalConnection>>([]);
    public Task<ExternalFlowStart?> BeginAsync(string provider, string purpose, string? session, CancellationToken ct) => Task.FromResult<ExternalFlowStart?>(null);
    public Task<ExternalFlowView?> PendingAsync(Guid id, string? secret, string? session, CancellationToken ct) => Task.FromResult<ExternalFlowView?>(null);
    public Task<ExternalFlowCompletion> CompleteAsync(Guid id, string? secret, string? session, CancellationToken ct) => Task.FromResult(new ExternalFlowCompletion(false));
    public Task<bool> UnlinkAsync(string provider, string? session, CancellationToken ct) => Task.FromResult(false);
}
