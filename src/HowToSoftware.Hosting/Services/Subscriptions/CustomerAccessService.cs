using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Subscriptions;

public interface ICustomerAccess
{
    bool Enabled { get; }
    Task<bool> RequestAsync(string email, CancellationToken ct);
    Task<string?> ConfirmAsync(string token, CancellationToken ct);
    Task<string?> OwnerAsync(string? session, CancellationToken ct);
    Task RevokeAsync(string? session, CancellationToken ct);
}

/// <summary>Opaque, single-use challenges and revocable, bounded sessions. Only hashes persist.</summary>
public sealed class CustomerAccessService(IDbContextFactory<CommerceDbContext> factory,
    IOptions<CustomerAreaOptions> settings, IOptions<SmtpOptions> smtp, IOptions<SiteOptions> site,
    ITrialMailTransport transport, IStringLocalizer<CustomerText> text, TimeProvider clock) : ICustomerAccess
{
    public bool Enabled => settings.Value.Enabled && smtp.Value.IsConfigured;
    public async Task<bool> RequestAsync(string email, CancellationToken ct)
    {
        if (!Enabled) return false;
        var normalized = TrialService.NormalizeEmail(email);
        if (normalized is null) return true; // Never disclose whether a customer exists.
        var now = clock.GetUtcNow();
        var token = NewToken();
        var hash = Hash(token);
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.CustomerAccess.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
        if (existing is null)
        {
            db.CustomerAccess.Add(new() { Id = Guid.NewGuid(), NormalizedEmail = normalized,
                ChallengeHash = hash, CreatedAt = now, ExpiresAt = now.AddMinutes(15) });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            { return true; }
        }
        else if (await db.CustomerAccess.Where(x => x.Id == existing.Id && x.CreatedAt <= now.AddMinutes(-2))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ChallengeHash, hash).SetProperty(x => x.CreatedAt, now)
                .SetProperty(x => x.ExpiresAt, now.AddMinutes(15)).SetProperty(x => x.UsedAt, (DateTimeOffset?)null), ct) != 1)
            return true;

        var link = site.Value.BaseUrl.TrimEnd('/') + "/account?verify=" + Uri.EscapeDataString(token);
        // A configured origin, never the caller's Host/X-Forwarded-Host, controls email links.
        await transport.DeliverAsync(new(normalized, text["Portal.SmtpSubject"], text["Portal.SmtpBody", link]), ct);
        return true;
    }

    public async Task<string?> ConfirmAsync(string token, CancellationToken ct)
    {
        if (!Enabled || !ValidToken(token)) return null;
        var now = clock.GetUtcNow();
        var session = NewToken();
        var hash = Hash(token);
        await using var db = await factory.CreateDbContextAsync(ct);
        var changed = await db.CustomerAccess.Where(x => x.ChallengeHash == hash && x.UsedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.UsedAt, now).SetProperty(x => x.SessionHash, Hash(session))
                .SetProperty(x => x.SessionExpiresAt, now.AddMinutes(30)), ct);
        return changed == 1 ? session : null;
    }

    public async Task<string?> OwnerAsync(string? session, CancellationToken ct)
    {
        if (!Enabled || !ValidToken(session)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var hash = Hash(session!);
        var now = clock.GetUtcNow();
        // UsedAt belongs to the email challenge, not the separately issued session. Merely
        // requesting a new email must not let an anonymous caller log an existing customer out.
        return await db.CustomerAccess.AsNoTracking().Where(x => x.SessionHash == hash && x.SessionExpiresAt > now)
            .Select(x => x.NormalizedEmail).SingleOrDefaultAsync(ct);
    }

    public async Task RevokeAsync(string? session, CancellationToken ct)
    {
        if (!ValidToken(session)) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var hash = Hash(session!);
        await db.CustomerAccess.Where(x => x.SessionHash == hash).ExecuteUpdateAsync(u =>
            u.SetProperty(x => x.SessionHash, (string?)null).SetProperty(x => x.SessionExpiresAt, (DateTimeOffset?)null), ct);
    }
    internal static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    internal static bool ValidToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);
}

public sealed class UnavailableCustomerAccess : ICustomerAccess
{
    public bool Enabled => false;
    public Task<bool> RequestAsync(string email, CancellationToken ct) => Task.FromResult(false);
    public Task<string?> ConfirmAsync(string token, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> OwnerAsync(string? session, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task RevokeAsync(string? session, CancellationToken ct) => Task.CompletedTask;
}
