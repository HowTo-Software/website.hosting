using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Trials;

namespace HowToSoftware.Hosting.Services.Subscriptions;

public sealed class CustomerAreaOptions
{
    public bool Enabled { get; set; }
    public bool PreviewEnabled { get; set; }
}

public sealed class SubscriptionManagementOptions
{
    public bool Enabled { get; set; }
    public bool ReconciliationEnabled { get; set; }
    public int PollSeconds { get; set; } = 60;
    public int QuoteMinutes { get; set; } = 5;
}

public sealed record ManagedService(Guid Id, Guid OrderId, Guid GameId, string GameSlug,
    CommerceHostingPlan Plan, string BillingPeriod, string SubscriptionId, string CustomerId,
    string OwnerEmail, int? ServerId, string? ServerUuid, string Currency = "usd");
public sealed record ManagedPlan(CommerceHostingPlan Plan, string? StripePriceId);
public sealed record SubscriptionQuoteResult(bool Success, string Code, SubscriptionPlanChange? Change = null);

public interface ISubscriptionStore
{
    Task<IReadOnlyList<ManagedService>> ListOwnedAsync(string email, CancellationToken ct);
    Task<ManagedService?> FindServiceAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ManagedPlan>> ListPlansAsync(Guid gameId, string period, CancellationToken ct);
    async Task<IReadOnlyList<ManagedPlan>> ListPlansAsync(Guid gameId, string period, string currency, CancellationToken ct) =>
        currency == "usd" ? await ListPlansAsync(gameId, period, ct) : [];
    Task<SubscriptionPlanChange?> FindChangeAsync(Guid id, CancellationToken ct);
    Task<SubscriptionPlanChange?> ActiveChangeAsync(Guid serviceId, CancellationToken ct);
    Task<bool> AddChangeAsync(SubscriptionPlanChange change, CancellationToken ct);
    Task<SubscriptionPlanChange?> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken ct);
    Task<bool> SaveAsync(SubscriptionPlanChange change, Guid expectedLeaseToken, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct);
    Task SetAppliedPlanAsync(Guid serviceId, Guid planId, CancellationToken ct);
}

public interface ISubscriptionManagement
{
    bool Enabled { get; }
    Task<SubscriptionQuoteResult> PreviewAsync(string owner, Guid serviceId, Guid targetPlan, CancellationToken ct);
    Task<SubscriptionQuoteResult> ConfirmAsync(string owner, Guid changeId, CancellationToken ct);
    Task<bool> CancelScheduledAsync(string owner, Guid changeId, CancellationToken ct);
    Task ReconcileAsync(string? subscriptionId, CancellationToken ct);
}

public interface ISubscriptionResources
{
    Task<bool> CanUpgradeAsync(ManagedService service, TrialResourceLimits target, CancellationToken ct);
    Task ApplyAsync(ManagedService service, SubscriptionPlanChange change, CancellationToken ct);
}

public static class PlanChangeRules
{
    public static bool SameInterval(string period, string? interval, long count) =>
        (period, interval, count) is ("monthly", "month", 1) or ("quarterly", "month", 3) or ("annual", "year", 1);

    public static PlanChangeDirection? Direction(CommerceHostingPlan current, CommerceHostingPlan target, long currentPrice, long targetPrice)
    {
        static long Cpu(int percent) => percent == 0 ? long.MaxValue : percent;
        var before = new long[] { current.RamMb, Cpu(current.CpuPercent), current.DiskMb, current.BackupLimit, current.DatabaseLimit, current.AllocationLimit };
        var after = new long[] { target.RamMb, Cpu(target.CpuPercent), target.DiskMb, target.BackupLimit, target.DatabaseLimit, target.AllocationLimit };
        if (targetPrice > currentPrice && before.Zip(after).All(x => x.Second >= x.First)) return PlanChangeDirection.Upgrade;
        if (targetPrice < currentPrice && before.Zip(after).All(x => x.Second <= x.First)) return PlanChangeDirection.Downgrade;
        return null;
    }

    public static TrialResourceLimits Limits(CommerceHostingPlan plan) => new(plan.RamMb, plan.CpuPercent,
        plan.DiskMb, plan.BackupLimit, plan.DatabaseLimit, Math.Max(1, plan.AllocationLimit));
    public static TrialResourceLimits Limits(SubscriptionPlanChange change) => new(change.MemoryMb, change.CpuPercent,
        change.DiskMb, change.BackupLimit, change.DatabaseLimit, Math.Max(1, change.AllocationLimit));
}
