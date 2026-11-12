using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Models.Commerce;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HowToSoftware.Hosting.Services.Trials;

/// <summary>SQL Server durable entitlement, exclusivity leases and payment-aware deletion fences.</summary>
public sealed class SqlServerTrialStore(IDbContextFactory<CommerceDbContext> factory) : ITrialStore
{
    public bool IsConfigured => true;

    private async Task<ServerTrialRecord?> FindAsync(System.Linq.Expressions.Expression<Func<ServerTrialRecord, bool>> predicate, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ServerTrials.AsNoTracking().SingleOrDefaultAsync(predicate, ct);
    }

    public Task<ServerTrialRecord?> FindByEmailAsync(string email, CancellationToken ct) => FindAsync(x => x.NormalizedEmail == email, ct);
    public Task<ServerTrialRecord?> FindByVerificationAsync(string hash, CancellationToken ct) => FindAsync(x => x.VerificationTokenHash == hash, ct);
    public Task<ServerTrialRecord?> FindByAccessAsync(string hash, CancellationToken ct) => FindAsync(x => x.AccessTokenHash == hash, ct);
    public async Task<ServerTrialRecord?> FindByOrderAsync(Guid orderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ServerTrials.AsNoTracking().SingleOrDefaultAsync(x =>
            db.TrialUpgradeOrders.Any(link => link.TrialId == x.Id && link.OrderId == orderId), ct);
    }

    public async Task<bool> AddPendingAsync(ServerTrialRecord record, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ServerTrials.Add(record);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException error) when (IsDuplicate(error)) { return false; }
    }

    public async Task<bool> RefreshVerificationAsync(ServerTrialRecord record, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ServerTrials.Where(x => x.Id == record.Id && x.State != TrialState.Rejected
                && (x.LeaseToken == null || x.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.VerificationTokenHash, record.VerificationTokenHash)
                .SetProperty(x => x.VerificationExpiresAt, record.VerificationExpiresAt)
                .SetProperty(x => x.Locale, record.Locale)
                .SetProperty(x => x.GameSlug, x => x.VerifiedAt == null ? record.GameSlug : x.GameSlug)
                .SetProperty(x => x.ProfileId, x => x.VerifiedAt == null ? record.ProfileId : x.ProfileId)
                .SetProperty(x => x.Version, x => x.VerifiedAt == null ? record.Version : x.Version)
                .SetProperty(x => x.UpdatedAt, now), ct) == 1;
    }

    public async Task ClearVerificationAsync(Guid id, string verificationHash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.ServerTrials.Where(x => x.Id == id && x.VerificationTokenHash == verificationHash)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.VerificationTokenHash, (string?)null)
                .SetProperty(x => x.VerificationExpiresAt, (DateTimeOffset?)null), ct);
    }

    public async Task<bool> ConfirmAsync(Guid id, string verificationHash, string accessHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ServerTrials.Where(x => x.Id == id && x.VerificationTokenHash == verificationHash
                && x.VerificationExpiresAt > now && x.State != TrialState.Rejected
                && (x.LeaseToken == null || x.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.State, x => x.VerifiedAt == null ? TrialState.Provisioning : x.State)
                .SetProperty(x => x.VerifiedAt, x => x.VerifiedAt ?? now)
                .SetProperty(x => x.AccessTokenHash, accessHash)
                .SetProperty(x => x.VerificationTokenHash, (string?)null)
                .SetProperty(x => x.VerificationExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), ct) == 1;
    }

    public async Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ServerTrials.AsNoTracking().Where(x =>
                (x.LeaseToken == null || x.LeaseExpiresAt <= now)
                && (x.NextAttemptAt == null || x.NextAttemptAt <= now)
                && (x.State == TrialState.Provisioning || x.State == TrialState.Installing || x.State == TrialState.Deleting
                    || (x.State == TrialState.Active && (x.ExpiresAt <= now || x.ReadyEmailSentAt == null))
                    || (x.State == TrialState.Expired && (x.DeleteAfter <= now || x.ExpiredEmailSentAt == null))
                    || (x.State == TrialState.Deleted && x.DeletedEmailSentAt == null)
                    || (x.UpgradeOrderId != null && x.ReservationExpiresAt <= now)))
            .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(100).ToArrayAsync(ct);
    }

    public async Task<ServerTrialRecord?> ClaimAsync(Guid id, DateTimeOffset now, TimeSpan duration, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var token = Guid.NewGuid();
        var until = now + duration;
        var affected = await db.ServerTrials.Where(x => x.Id == id && x.State != TrialState.Converted
                && x.State != TrialState.Rejected && x.State != TrialState.PendingVerification
                && (x.LeaseToken == null || x.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseToken, (Guid?)token)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)until), ct);
        return affected == 1 ? await db.ServerTrials.AsNoTracking().SingleAsync(x => x.Id == id, ct) : null;
    }

    public async Task<bool> RenewAsync(Guid id, Guid token, DateTimeOffset now, TimeSpan duration, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var until = now + duration;
        return await db.ServerTrials.Where(x => x.Id == id && x.LeaseToken == token && x.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)until), ct) == 1;
    }

    public async Task<bool> SaveLeasedAsync(ServerTrialRecord record, Guid token, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.ServerTrials.AnyAsync(x => x.Id == record.Id && x.LeaseToken == token && x.LeaseExpiresAt > now, ct))
            return false;
        // The lease token is a concurrency token: a replacement owner cannot be overwritten.
        record.UpdatedAt = now;
        db.ServerTrials.Attach(record);
        db.Entry(record).Property(x => x.LeaseToken).OriginalValue = token;
        // Owner verification and access hashes are independent of lifecycle processing.
        // Updating only lifecycle fields prevents a stale worker snapshot from revoking a recovered login.
        string[] lifecycleFields =
        [
            nameof(ServerTrialRecord.State), nameof(ServerTrialRecord.PterodactylUserId),
            nameof(ServerTrialRecord.PterodactylServerId), nameof(ServerTrialRecord.ServerIdentifier),
            nameof(ServerTrialRecord.ServerUuid), nameof(ServerTrialRecord.UpdatedAt),
            nameof(ServerTrialRecord.StartedAt), nameof(ServerTrialRecord.ExpiresAt),
            nameof(ServerTrialRecord.SuspendedAt), nameof(ServerTrialRecord.DeleteAfter),
            nameof(ServerTrialRecord.DeletedAt), nameof(ServerTrialRecord.ConvertedAt),
            nameof(ServerTrialRecord.UpgradeOrderId), nameof(ServerTrialRecord.ReservationExpiresAt),
            nameof(ServerTrialRecord.LeaseExpiresAt), nameof(ServerTrialRecord.NextAttemptAt),
            nameof(ServerTrialRecord.ReadyEmailSentAt), nameof(ServerTrialRecord.ExpiredEmailSentAt),
            nameof(ServerTrialRecord.DeletedEmailSentAt), nameof(ServerTrialRecord.LastFailureClass)
        ];
        foreach (var field in lifecycleFields) db.Entry(record).Property(field).IsModified = true;
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (IsDuplicate(error)) { throw new TrialAccountAlreadyClaimedException(); }
    }

    public async Task ReleaseLeaseAsync(Guid id, Guid token, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.ServerTrials.Where(x => x.Id == id && x.LeaseToken == token)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), ct);
    }

    public async Task<bool> ReserveUpgradeAsync(Guid id, string accessHash, Guid orderId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var strategyContext = await factory.CreateDbContextAsync(ct);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(
            () => ReserveCoreAsync(id, accessHash, orderId, now, expiresAt, ct));
    }

    private async Task<bool> ReserveCoreAsync(Guid id, string accessHash, Guid orderId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Pending orders must already exist; ownership is established by the private access token.
        if (!await db.Orders.AnyAsync(x => x.Id == orderId, ct)) return false;
        var reserved = await db.ServerTrials.Where(x => x.Id == id && x.AccessTokenHash == accessHash
                && (x.State == TrialState.Active || x.State == TrialState.Expired)
                && (x.State != TrialState.Expired || x.DeleteAfter > now)
                && (x.DeleteAfter == null || x.DeleteAfter > now)
                && x.PterodactylServerId != null && (x.UpgradeOrderId == null || x.UpgradeOrderId == orderId)
                && (x.LeaseToken == null || x.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.UpgradeOrderId, (Guid?)orderId)
                .SetProperty(x => x.ReservationExpiresAt, (DateTimeOffset?)expiresAt), ct) == 1;
        if (!reserved) return false;
        var prior = await db.TrialUpgradeOrders.SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (prior is not null && prior.TrialId != id) return false;
        if (prior is null)
        {
            db.TrialUpgradeOrders.Add(new TrialUpgradeOrderRecord { OrderId = orderId, TrialId = id, CreatedAt = now });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task ReleaseUpgradeAsync(Guid orderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.ServerTrials.Where(x => x.UpgradeOrderId == orderId && x.State != TrialState.Converted && x.LeaseToken == null
                && !db.Orders.Any(o => o.Id == orderId && (o.PaidAt != null || o.Status == "Paid" || o.Status == "Provisioning" || o.Status == "Active")))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.UpgradeOrderId, (Guid?)null)
                .SetProperty(x => x.ReservationExpiresAt, (DateTimeOffset?)null), ct);
    }

    public async Task<bool> IsPaidOrderAsync(Guid orderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Orders.AnyAsync(x => x.Id == orderId &&
            (x.PaidAt != null || x.Status == "Paid" || x.Status == "Provisioning" || x.Status == "Active"), ct);
    }

    public async Task<Guid?> FindPaidUpgradeAsync(Guid trialId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TrialUpgradeOrders.Where(link => link.TrialId == trialId
                && db.Orders.Any(o => o.Id == link.OrderId &&
                    (o.PaidAt != null || o.Status == "Paid" || o.Status == "Provisioning" || o.Status == "Active")))
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.OrderId).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> ClearUnpaidReservationAsync(Guid id, Guid token, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ServerTrials.Where(x => x.Id == id && x.LeaseToken == token && x.LeaseExpiresAt > now
                && x.UpgradeOrderId != null && x.ReservationExpiresAt <= now
                && !db.Orders.Any(o => o.Id == x.UpgradeOrderId &&
                    (o.PaidAt != null || o.Status == "Paid" || o.Status == "Provisioning" || o.Status == "Active")))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.UpgradeOrderId, (Guid?)null)
                .SetProperty(x => x.ReservationExpiresAt, (DateTimeOffset?)null), ct) == 1;
    }

    public async Task<bool> BeginDeleteAsync(Guid id, Guid token, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ServerTrials.Where(x => x.Id == id && x.State == TrialState.Expired
                && x.DeleteAfter <= now && x.UpgradeOrderId == null && x.LeaseToken == token && x.LeaseExpiresAt > now
                && !db.TrialUpgradeOrders.Any(link => link.TrialId == x.Id && db.Orders.Any(o => o.Id == link.OrderId
                    && (o.PaidAt != null || o.Status == "Paid" || o.Status == "Provisioning" || o.Status == "Active"))))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, TrialState.Deleting), ct) == 1;
    }

    private static bool IsDuplicate(DbUpdateException error) => error.InnerException is SqlException { Number: 2601 or 2627 };
}
