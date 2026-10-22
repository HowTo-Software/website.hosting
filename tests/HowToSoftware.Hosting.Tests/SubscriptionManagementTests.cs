using System.Text.Json;
using HowToSoftware.Hosting.Infrastructure.Stripe;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Subscriptions;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Stripe;

namespace HowToSoftware.Hosting.Tests;

/// <summary>Hermetic financial state machine checks. Never uses Stripe, SMTP or a panel.</summary>
public sealed class SubscriptionManagementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PixMandatesNeverUseTheCardOnlyPlanMutationFlow(bool inheritedFromCustomer)
    {
        var h = new Harness();
        var method = new PaymentMethod { Id = "pm_test_pix", Type = "pix" };
        if (inheritedFromCustomer)
            h.Stripe.Sub.Customer = new Customer { Id = "cus_test", InvoiceSettings = new() { DefaultPaymentMethod = method } };
        else h.Stripe.Sub.DefaultPaymentMethod = method;
        Assert.Equal("payment-method", (await h.Preview()).Code);
        Assert.Empty(h.Store.Changes);
        Assert.Equal(0, h.Stripe.Upgrades);
        Assert.Equal(0, h.Resources.Applies);
    }

    [Fact]
    public async Task SwitchingToPixAfterAQuoteCancelsItWithoutCharging()
    {
        var h = new Harness();
        var quoted = (await h.Preview()).Change!;
        h.Stripe.Sub.DefaultPaymentMethod = new PaymentMethod { Id = "pm_test_pix", Type = "pix" };
        Assert.Equal("payment-method", (await h.Service.ConfirmAsync(Harness.Owner, quoted.Id, default)).Code);
        Assert.Equal(PlanChangeState.Cancelled, h.Store.Changes[quoted.Id].State);
        Assert.Equal(0, h.Stripe.Upgrades);
        Assert.Equal(0, h.Resources.Applies);
    }

    [Fact]
    public async Task PreviewUsesExactNativeAmountsAndFrozenTimestampNotLocalProration()
    {
        var h = new Harness();
        h.Stripe.Due = 113; h.Stripe.Recurring = 1749; // Deliberately not a price-times-time ratio.
        var result = await h.Preview();
        Assert.True(result.Success);
        Assert.Equal(113, result.Change!.DueNowMinor);
        Assert.Equal(1749, result.Change.NextInvoiceMinor);
        Assert.Equal(h.Clock.GetUtcNow().ToUnixTimeSeconds(), result.Change.ProrationAt.ToUnixTimeSeconds());
        Assert.Equal(0, h.Stripe.Upgrades);
        Assert.Equal(0, h.Resources.Applies);
    }

    [Fact]
    public async Task NearRenewalQuoteCannotCrossBillingBoundaryOnConfirmation()
    {
        var h = new Harness();
        h.Stripe.Sub.Items.Data[0].CurrentPeriodEnd = h.Clock.GetUtcNow().AddSeconds(62).UtcDateTime;
        var quote = await h.Preview();
        Assert.True(quote.Success);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        var result = await h.Service.ConfirmAsync(Harness.Owner, quote.Change!.Id, default);
        Assert.Equal("renewing", result.Code);
        Assert.Equal(0, h.Stripe.Upgrades);
    }

    [Fact]
    public async Task RenewalAlreadyInProgressDoesNotCreateAQuoteOrCharge()
    {
        var h = new Harness();
        h.Stripe.Sub.Items.Data[0].CurrentPeriodEnd = h.Clock.GetUtcNow().AddSeconds(30).UtcDateTime;
        Assert.Equal("renewing", (await h.Preview()).Code);
        Assert.Empty(h.Store.Changes);
    }

    [Fact]
    public async Task CrossCustomerIdsNeverAuthorizeFinancialAccess()
    {
        var h = new Harness();
        Assert.False((await h.Service.PreviewAsync("other@example.test", h.Managed.Id, h.Target.Id, default)).Success);
        var q = (await h.Preview()).Change!;
        Assert.False((await h.Service.ConfirmAsync("other@example.test", q.Id, default)).Success);
        Assert.False(await h.Service.CancelScheduledAsync("other@example.test", q.Id, default));
        Assert.Equal(0, h.Stripe.Upgrades);
        Assert.Equal(PlanChangeState.Quoted, h.Store.Changes[q.Id].State);
    }

    [Fact]
    public async Task UpgradeWaitsForPaidInvoiceAndPreservesServiceAndServerIdentity()
    {
        var h = new Harness();
        var quote = (await h.Preview()).Change!;
        await h.Service.ConfirmAsync(Harness.Owner, quote.Id, default);
        Assert.Equal(PlanChangeState.AwaitingPayment, h.Store.Changes[quote.Id].State);
        Assert.Equal(0, h.Resources.Applies);
        await h.Service.ReconcileAsync("sub_test", default);
        Assert.Equal(0, h.Resources.Applies);
        h.Stripe.CompletePayment();
        await h.Service.ReconcileAsync("sub_test", default);
        Assert.Equal(1, h.Resources.Applies);
        Assert.Equal(h.Managed.Id, h.Resources.LastService!.Id);
        Assert.Equal(42, h.Resources.LastService.ServerId);
        Assert.Equal("same-server-uuid", h.Resources.LastService.ServerUuid);
        Assert.Equal(h.Target.Id, h.Store.AppliedPlan);
        Assert.Equal(PlanChangeState.Applied, h.Store.Changes[quote.Id].State);
        Assert.False(h.Store.Changes[quote.Id].Active);
    }

    [Fact]
    public async Task RefusedThenLatePaymentAndDuplicateOutOfOrderEventsDoNotDuplicateChargeOrApply()
    {
        var h = new Harness();
        var q = (await h.Preview()).Change!;
        await h.Service.ConfirmAsync(Harness.Owner, q.Id, default);
        await h.Service.ReconcileAsync(null, default);
        await h.Service.ReconcileAsync("sub_test", default);
        Assert.Equal(0, h.Resources.Applies);
        h.Stripe.CompletePayment();
        await h.Service.ReconcileAsync("sub_test", default);
        await h.Service.ReconcileAsync("sub_test", default);
        await h.Service.ReconcileAsync(null, default); // An old event only triggers a fresh read.
        Assert.Equal(1, h.Stripe.Upgrades);
        Assert.Equal(1, h.Resources.Applies);
    }

    [Fact]
    public async Task SimultaneousConfirmationOnlySubmitsOneUpgrade()
    {
        var h = new Harness();
        var q = (await h.Preview()).Change!;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Service.ConfirmAsync(Harness.Owner, q.Id, default)));
        Assert.Equal(1, h.Stripe.Upgrades);
    }

    [Fact]
    public async Task CrashAfterStripeAcknowledgesRecoversWithoutSecondCharge()
    {
        var h = new Harness();
        var q = (await h.Preview()).Change!;
        h.Stripe.ThrowAfterUpgrade = true;
        await Assert.ThrowsAsync<TimeoutException>(() => h.Service.ConfirmAsync(Harness.Owner, q.Id, default));
        Assert.Equal(PlanChangeState.Submitting, h.Store.Changes[q.Id].State);
        h.Stripe.CompletePayment();
        await h.Service.ReconcileAsync(null, default);
        Assert.Equal(1, h.Stripe.Upgrades);
        Assert.Equal(1, h.Resources.Applies);
    }

    [Fact]
    public async Task PanelFailureRetriesSameBuildWithoutRecharging()
    {
        var h = new Harness();
        var q = (await h.Preview()).Change!;
        await h.Service.ConfirmAsync(Harness.Owner, q.Id, default);
        h.Stripe.CompletePayment(); h.Resources.FailOnce = true;
        await h.Service.ReconcileAsync(null, default);
        Assert.True(h.Store.Changes[q.Id].Active);
        await h.Service.ReconcileAsync(null, default);
        Assert.Equal(2, h.Resources.Applies);
        Assert.Equal(1, h.Stripe.Upgrades);
        Assert.Equal(PlanChangeState.Applied, h.Store.Changes[q.Id].State);
    }

    [Fact]
    public async Task DowngradeKeepsCurrentResourcesUntilPaidRenewal()
    {
        var h = new Harness(downgrade: true);
        var q = (await h.Preview()).Change!;
        Assert.Equal(0, q.DueNowMinor);
        Assert.Equal(PlanChangeDirection.Downgrade, q.Direction);
        await h.Service.ConfirmAsync(Harness.Owner, q.Id, default);
        Assert.Equal(1, h.Stripe.Schedules);
        Assert.Equal(0, h.Stripe.Upgrades);
        await h.Service.ReconcileAsync(null, default);
        Assert.Equal(0, h.Resources.Applies);
        h.Clock.Advance(TimeSpan.FromDays(31));
        h.Stripe.Sub.Items.Data[0].Price = h.Stripe.TargetPrice;
        h.Stripe.Sub.PendingUpdate = null;
        h.Stripe.Sub.LatestInvoiceId = "in_renewal";
        h.Stripe.Invoice = h.Stripe.NewInvoice("in_renewal", "open", q.EffectiveAt.UtcDateTime);
        await h.Service.ReconcileAsync(null, default);
        Assert.Equal(0, h.Resources.Applies);
        h.Stripe.Invoice.Status = "paid";
        await h.Service.ReconcileAsync(null, default);
        Assert.Equal(1, h.Resources.Applies);
        Assert.Equal(PlanChangeState.Applied, h.Store.Changes[q.Id].State);
    }

    [Fact]
    public async Task CancellationReleasesScheduleAndNeverCancelsTheSubscription()
    {
        var h = new Harness(downgrade: true);
        var q = (await h.Preview()).Change!;
        await h.Service.ConfirmAsync(Harness.Owner, q.Id, default);
        Assert.True(await h.Service.CancelScheduledAsync(Harness.Owner, q.Id, default));
        Assert.Equal(1, h.Stripe.Releases);
        Assert.Equal("active", h.Stripe.Sub.Status);
        Assert.Equal(0, h.Resources.Applies);
        Assert.False(h.Store.Changes[q.Id].Active);
    }

    [Fact]
    public async Task CancellationCannotReleaseAtOrAfterRenewalBoundary()
    {
        var h = new Harness(downgrade: true);
        var q = (await h.Preview()).Change!;
        await h.Service.ConfirmAsync(Harness.Owner, q.Id, default);
        h.Clock.SetUtcNow(q.EffectiveAt);
        Assert.False(await h.Service.CancelScheduledAsync(Harness.Owner, q.Id, default));
        Assert.Equal(0, h.Stripe.Releases);
    }

    [Fact]
    public async Task UnverifiedDiskReductionRequiresOperationalResolutionAndKeepsExclusivity()
    {
        var h = new Harness();
        var q = (await h.Preview()).Change!;
        await h.Service.ConfirmAsync(Harness.Owner, q.Id, default);
        h.Stripe.CompletePayment(); h.Resources.UnsafeDisk = true;
        await h.Service.ReconcileAsync(null, default);
        Assert.Equal(PlanChangeState.Operational, h.Store.Changes[q.Id].State);
        Assert.True(h.Store.Changes[q.Id].Active);
        Assert.Null(h.Store.AppliedPlan);
        Assert.Equal("pending", (await h.Preview()).Code);
    }

    [Theory]
    [InlineData("customer", "invalid")]
    [InlineData("interval", "interval")]
    [InlineData("unpaid", "unpaid")]
    [InlineData("capacity", "capacity")]
    [InlineData("disabled", "disabled")]
    [InlineData("mixed", "invalid")]
    public async Task UnsafeOrUnsupportedQuotesAreRejectedBeforeMutation(string scenario, string expected)
    {
        var h = new Harness();
        switch (scenario)
        {
            case "customer": h.Stripe.Sub.CustomerId = "cus_other"; break;
            case "interval": h.Stripe.TargetPrice.Recurring.Interval = "year"; break;
            case "unpaid": h.Stripe.Baseline.Status = "open"; break;
            case "capacity": h.Resources.Capacity = false; break;
            case "disabled": h.Options.Enabled = false; break;
            case "mixed": h.Target.BackupLimit = 0; break;
        }
        var result = await h.Preview();
        Assert.Equal(expected == "invalid" && scenario == "customer" ? "pending" : expected, result.Code);
        Assert.Equal(0, h.Stripe.Upgrades);
        Assert.Equal(0, h.Stripe.Schedules);
    }

    [Fact]
    public async Task QuoteExpiresAndCanNeverBeConfirmedLater()
    {
        var h = new Harness();
        var q = (await h.Preview()).Change!;
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("expired", (await h.Service.ConfirmAsync(Harness.Owner, q.Id, default)).Code);
        Assert.Equal(0, h.Stripe.Upgrades);
    }

    [Fact]
    public async Task DifferentCustomersPaidInvoiceNeverAuthorizesResources()
    {
        var h = new Harness();
        var q = (await h.Preview()).Change!;
        await h.Service.ConfirmAsync(Harness.Owner, q.Id, default);
        h.Stripe.CompletePayment(); h.Stripe.Invoice.CustomerId = "cus_other";
        await h.Service.ReconcileAsync(null, default);
        Assert.Equal(0, h.Resources.Applies);
    }

    [Fact]
    public async Task UnknownOldSubmissionIsNotRechargedAfterStripeIdempotencyWindow()
    {
        var h = new Harness();
        var q = (await h.Preview()).Change!;
        var record = h.Store.Changes[q.Id]; record.State = PlanChangeState.Submitting;
        h.Clock.Advance(TimeSpan.FromHours(24));
        await h.Service.ReconcileAsync(null, default);
        Assert.Equal(PlanChangeState.Operational, h.Store.Changes[q.Id].State);
        Assert.Equal(0, h.Stripe.Upgrades);
    }

    internal sealed class Harness
    {
        public const string Owner = "owner@example.test";
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        public readonly MemoryStore Store = new();
        public readonly FakeStripe Stripe;
        public readonly FakeResources Resources = new();
        public readonly CommerceHostingPlan Current;
        public readonly CommerceHostingPlan Target;
        public readonly ManagedService Managed;
        public readonly SubscriptionManagementOptions Options = new() { Enabled = true, ReconciliationEnabled = true };
        public readonly SubscriptionManagementService Service;
        public Harness(bool downgrade = false)
        {
            var game = Guid.NewGuid();
            Current = Plan(game, 4096); Target = Plan(game, downgrade ? 2048 : 8192);
            Managed = new(Guid.NewGuid(), Guid.NewGuid(), game, "project-zomboid", Current, "monthly", "sub_test", "cus_test", Owner, 42, "same-server-uuid");
            Store.Managed = Managed; Store.Target = Target;
            Stripe = new(Clock.GetUtcNow(), downgrade);
            Service = new(Store, Stripe, Resources, Microsoft.Extensions.Options.Options.Create(Options),
                Microsoft.Extensions.Options.Options.Create(new StripeOptions()), Clock, NullLogger<SubscriptionManagementService>.Instance);
        }
        public Task<SubscriptionQuoteResult> Preview() => Service.PreviewAsync(Owner, Managed.Id, Target.Id, default);
        private static CommerceHostingPlan Plan(Guid game, int ram) => new()
        {
            Id = Guid.NewGuid(),
            GameId = game,
            Slug = "plan-" + ram,
            Name = "Plan " + ram,
            Currency = "usd",
            RamMb = ram,
            CpuPercent = 300,
            DiskMb = 25600,
            AllocationLimit = 1,
            BackupLimit = 1,
            DatabaseLimit = 0,
            Active = true
        };
    }
    internal sealed class FakeResources : ISubscriptionResources
    {
        public bool Capacity = true, FailOnce, UnsafeDisk;
        public int Applies;
        public ManagedService? LastService;
        public Task<bool> CanUpgradeAsync(ManagedService s, TrialResourceLimits l, CancellationToken ct) => Task.FromResult(Capacity);
        public Task ApplyAsync(ManagedService s, SubscriptionPlanChange c, CancellationToken ct)
        {
            Applies++; LastService = s;
            if (UnsafeDisk) throw new UnsafeResourceReductionException();
            if (FailOnce) { FailOnce = false; throw new TimeoutException(); }
            return Task.CompletedTask;
        }
    }
    internal sealed class MemoryStore : ISubscriptionStore
    {
        public ManagedService Managed = null!;
        public CommerceHostingPlan Target = null!;
        public readonly Dictionary<Guid, SubscriptionPlanChange> Changes = [];
        public Guid? AppliedPlan;
        private readonly object _gate = new();
        private static SubscriptionPlanChange Copy(SubscriptionPlanChange c) => JsonSerializer.Deserialize<SubscriptionPlanChange>(JsonSerializer.Serialize(c))!;
        public Task<IReadOnlyList<ManagedService>> ListOwnedAsync(string email, CancellationToken ct) => Task.FromResult<IReadOnlyList<ManagedService>>(email == Harness.Owner ? [Managed] : []);
        public Task<ManagedService?> FindServiceAsync(Guid id, CancellationToken ct) => Task.FromResult(id == Managed.Id ? Managed : null);
        public Task<IReadOnlyList<ManagedPlan>> ListPlansAsync(Guid gameId, string period, CancellationToken ct) => Task.FromResult<IReadOnlyList<ManagedPlan>>(gameId == Managed.GameId && period == Managed.BillingPeriod ? [new(Target, "price_target")] : []);
        public Task<SubscriptionPlanChange?> FindChangeAsync(Guid id, CancellationToken ct) { lock (_gate) return Task.FromResult(Changes.TryGetValue(id, out var c) ? Copy(c) : null); }
        public Task<SubscriptionPlanChange?> ActiveChangeAsync(Guid id, CancellationToken ct) { lock (_gate) return Task.FromResult(Changes.Values.FirstOrDefault(x => x.Active && x.HostingServiceId == id) is { } c ? Copy(c) : null); }
        public Task<bool> AddChangeAsync(SubscriptionPlanChange c, CancellationToken ct) { lock (_gate) { if (Changes.Values.Any(x => x.HostingServiceId == c.HostingServiceId && x.Active)) return Task.FromResult(false); Changes.Add(c.Id, Copy(c)); return Task.FromResult(true); } }
        public Task<SubscriptionPlanChange?> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken ct)
        {
            lock (_gate)
            {
                if (!Changes.TryGetValue(id, out var c) || !c.Active || c.LeaseToken is not null && c.LeaseExpiresAt > now) return Task.FromResult<SubscriptionPlanChange?>(null);
                c.LeaseToken = Guid.NewGuid(); c.LeaseExpiresAt = now.AddMinutes(5); return Task.FromResult<SubscriptionPlanChange?>(Copy(c));
            }
        }
        public Task<bool> SaveAsync(SubscriptionPlanChange c, Guid expected, CancellationToken ct)
        { lock (_gate) { if (Changes[c.Id].LeaseToken != expected) return Task.FromResult(false); Changes[c.Id] = Copy(c); return Task.FromResult(true); } }
        public Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
        { lock (_gate) return Task.FromResult<IReadOnlyList<Guid>>(Changes.Values.Where(c => c.Active && c.State != PlanChangeState.Operational && (c.State != PlanChangeState.Quoted || c.QuoteExpiresAt <= now)).Select(c => c.Id).ToArray()); }
        public Task SetAppliedPlanAsync(Guid id, Guid planId, CancellationToken ct) { AppliedPlan = planId; return Task.CompletedTask; }
    }
    internal sealed class FakeStripe : ISubscriptionGateway
    {
        public bool IsConfigured => true;
        public readonly Subscription Sub;
        public readonly Price CurrentPrice = Price("price_current", 1000);
        public readonly Price TargetPrice;
        public readonly Invoice Baseline;
        public Invoice Invoice;
        public long Due = 476, Recurring = 2000;
        public int Upgrades, Schedules, Releases;
        public bool ThrowAfterUpgrade;
        public FakeStripe(DateTimeOffset now, bool downgrade)
        {
            TargetPrice = Price("price_target", downgrade ? 500 : 2000);
            Sub = new()
            {
                Id = "sub_test",
                CustomerId = "cus_test",
                Status = "active",
                Currency = "usd",
                CollectionMethod = "charge_automatically",
                LatestInvoiceId = "in_baseline",
                Items = new() { Data = [new() { Id = "si_test", Price = CurrentPrice, Quantity = 1, CurrentPeriodStart = now.UtcDateTime, CurrentPeriodEnd = now.AddDays(30).UtcDateTime }] }
            };
            Baseline = NewInvoice("in_baseline", "paid", now.UtcDateTime); Invoice = Baseline;
        }
        private static Price Price(string id, long amount) => new() { Id = id, Active = true, UnitAmount = amount, Currency = "usd", Type = "recurring", BillingScheme = "per_unit", Recurring = new() { Interval = "month", IntervalCount = 1, UsageType = "licensed" } };
        public Invoice NewInvoice(string id, string status, DateTime start) => new()
        {
            Id = id,
            CustomerId = "cus_test",
            Currency = "usd",
            Status = status,
            AmountDue = Due,
            AmountRemaining = 0,
            Parent = new() { SubscriptionDetails = new() { SubscriptionId = "sub_test" } },
            Lines = new() { Data = [new() { Pricing = new() { PriceDetails = new() { PriceId = "price_target" } }, Period = new() { Start = start } }] }
        };
        public void CompletePayment() { Sub.PendingUpdate = null; Sub.Items.Data[0].Price = TargetPrice; Invoice.Status = "paid"; }
        public Task<Subscription> ReadSubscriptionAsync(string id, CancellationToken ct) => Task.FromResult(Sub);
        public Task<Price> ReadPriceAsync(string id, CancellationToken ct) => Task.FromResult(id == "price_target" ? TargetPrice : CurrentPrice);
        public Task<Invoice> PreviewAsync(string sub, string item, string price, DateTimeOffset at, bool recurring, CancellationToken ct) => Task.FromResult(new Invoice { AmountDue = recurring ? Recurring : Due, Currency = "usd" });
        public Task<Invoice> UpcomingAsync(string id, CancellationToken ct) => Task.FromResult(new Invoice { CustomerId = "cus_test", AmountDue = Recurring, Currency = "usd" });
        public Task<Invoice> ReadInvoiceAsync(string id, CancellationToken ct) => Task.FromResult(id == "in_baseline" ? Baseline : Invoice);
        public Task<IReadOnlyList<Invoice>> InvoicesAsync(string customer, string sub, CancellationToken ct) => Task.FromResult<IReadOnlyList<Invoice>>([Invoice]);
        public Task<Subscription> UpgradeAsync(SubscriptionPlanChange c, CancellationToken ct)
        {
            Upgrades++; Sub.PendingUpdate = new(); Sub.LatestInvoiceId = "in_upgrade"; Invoice = NewInvoice("in_upgrade", "open", c.ProrationAt.UtcDateTime);
            if (ThrowAfterUpgrade) throw new TimeoutException();
            return Task.FromResult(Sub);
        }
        public Task<SubscriptionSchedule> ScheduleAsync(SubscriptionPlanChange c, CancellationToken ct) { Schedules++; Sub.ScheduleId = "sched_test"; return Task.FromResult(new SubscriptionSchedule { Id = "sched_test" }); }
        public Task ReleaseAsync(SubscriptionPlanChange c, CancellationToken ct) { Releases++; Sub.ScheduleId = null; return Task.CompletedTask; }
    }
}
