using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Models.Commerce;
using Microsoft.EntityFrameworkCore;

namespace HowToSoftware.Hosting.Services.Subscriptions;

public sealed class SubscriptionStore(IDbContextFactory<CommerceDbContext> factory) : ISubscriptionStore
{
    public async Task<IReadOnlyList<ManagedService>> ListOwnedAsync(string email, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Services(db, email: email).ToListAsync(ct);
    }

    public async Task<ManagedService?> FindServiceAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Services(db, id: id).SingleOrDefaultAsync(ct);
    }

    private static IQueryable<ManagedService> Services(CommerceDbContext db, Guid? id = null, string? email = null) =>
        from service in db.HostingServices.AsNoTracking()
        join order in db.Orders.AsNoTracking() on service.OrderId equals order.Id
        join plan in db.HostingPlans.AsNoTracking() on service.PlanId equals plan.Id
        where order.CustomerEmail != null && service.StripeSubscriptionId != null && order.StripeCustomerId != null
            && (id == null || service.Id == id)
            && (email == null || order.CustomerEmail.Trim().ToLower() == email)
        select new ManagedService(service.Id, order.Id, order.GameId, order.GameSlug, plan, order.BillingPeriod,
            service.StripeSubscriptionId!, order.StripeCustomerId!, order.CustomerEmail!, service.PterodactylServerId,
            service.PterodactylServerUuid);

    public async Task<IReadOnlyList<ManagedPlan>> ListPlansAsync(Guid gameId, string period, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await (from plan in db.HostingPlans.AsNoTracking()
                      join price in db.PlanBillingPrices.AsNoTracking() on plan.Id equals price.PlanId
                      where plan.GameId == gameId && plan.Active && price.Active && price.BillingPeriod == period
                      orderby plan.SortOrder
                      select new ManagedPlan(plan, price.StripePriceId)).ToListAsync(ct);
    }

    public async Task<SubscriptionPlanChange?> FindChangeAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SubscriptionPlanChanges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<SubscriptionPlanChange?> ActiveChangeAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SubscriptionPlanChanges.AsNoTracking().SingleOrDefaultAsync(x => x.HostingServiceId == id && x.Active, ct);
    }

    public async Task<bool> AddChangeAsync(SubscriptionPlanChange change, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.SubscriptionPlanChanges.Add(change);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { return false; } // The filtered unique index serializes a service's requests.
    }

    public async Task<SubscriptionPlanChange?> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var token = Guid.NewGuid();
        var changed = await db.SubscriptionPlanChanges.Where(x => x.Id == id && x.Active
            && (x.LeaseToken == null || x.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LeaseToken, token)
                .SetProperty(x => x.LeaseExpiresAt, now.AddMinutes(5)), ct);
        return changed == 1 ? await db.SubscriptionPlanChanges.AsNoTracking().SingleAsync(x => x.Id == id, ct) : null;
    }

    public async Task<bool> SaveAsync(SubscriptionPlanChange change, Guid expectedLeaseToken, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Attach(change);
        db.Entry(change).State = EntityState.Modified;
        db.Entry(change).Property(x => x.LeaseToken).OriginalValue = expectedLeaseToken;
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    public async Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SubscriptionPlanChanges.AsNoTracking().Where(x => x.Active
            && x.State != PlanChangeState.Operational
            && (x.State != PlanChangeState.Quoted || x.QuoteExpiresAt <= now)
            && (x.LeaseToken == null || x.LeaseExpiresAt <= now))
            .OrderBy(x => x.UpdatedAt).Select(x => x.Id).Take(50).ToListAsync(ct);
    }

    public async Task SetAppliedPlanAsync(Guid id, Guid planId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.HostingServices.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.PlanId, planId), ct);
    }
}
