using global::Stripe;
using HowToSoftware.Hosting.Infrastructure.Stripe;
using HowToSoftware.Hosting.Models.Commerce;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Subscriptions;

/// <summary>Stripe is the financial authority; SQL coordinates changes, never the browser.</summary>
public sealed class SubscriptionManagementService(ISubscriptionStore store, ISubscriptionGateway stripe,
    ISubscriptionResources resources, IOptions<SubscriptionManagementOptions> settings,
    IOptions<StripeOptions> prices, TimeProvider clock, ILogger<SubscriptionManagementService> logger) : ISubscriptionManagement
{
    public bool Enabled => settings.Value.Enabled && settings.Value.ReconciliationEnabled && stripe.IsConfigured;

    public async Task<SubscriptionQuoteResult> PreviewAsync(string owner, Guid serviceId, Guid targetId, CancellationToken ct)
    {
        if (!Enabled) return Fail("disabled");
        var service = await OwnedAsync(owner, serviceId, ct);
        if (service is null) return Fail("invalid");
        if (await store.ActiveChangeAsync(serviceId, ct) is { } existing)
            return existing.State == PlanChangeState.Quoted && existing.TargetPlanId == targetId && existing.QuoteExpiresAt > clock.GetUtcNow()
                ? new(true, "quoted", existing) : Fail("pending");
        var target = (await store.ListPlansAsync(service.GameId, service.BillingPeriod, ct)).SingleOrDefault(x => x.Plan.Id == targetId);
        var priceId = target?.StripePriceId ?? (target is null ? null : prices.Value.FindPriceId(target.Plan.Slug, service.BillingPeriod));
        if (target is null || target.Plan.Id == service.Plan.Id || string.IsNullOrEmpty(priceId)) return Fail("invalid");
        var subscription = await stripe.ReadSubscriptionAsync(service.SubscriptionId, ct);
        var item = ValidSubscription(service, subscription);
        if (item is null || subscription.PendingUpdate is not null || subscription.ScheduleId is not null
            || subscription.CancelAtPeriodEnd || subscription.PauseCollection is not null) return Fail("pending");
        var latest = subscription.LatestInvoiceId is { } latestId ? await stripe.ReadInvoiceAsync(latestId, ct) : null;
        if (latest is null || !Paid(latest, service, subscription.Currency)) return Fail("unpaid");
        var currentPrice = await stripe.ReadPriceAsync(item.Price.Id, ct);
        var price = await stripe.ReadPriceAsync(priceId, ct);
        if (!ValidPrice(price, service.BillingPeriod, subscription.Currency) || !ValidPrice(currentPrice, service.BillingPeriod, subscription.Currency))
            return Fail("interval");
        var direction = PlanChangeRules.Direction(service.Plan, target.Plan, currentPrice.UnitAmount!.Value, price.UnitAmount!.Value);
        if (direction is null) return Fail("invalid");
        if (direction == PlanChangeDirection.Upgrade && !await resources.CanUpgradeAsync(service, PlanChangeRules.Limits(target.Plan), ct))
            return Fail("capacity");
        var now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        var renewal = new DateTimeOffset(DateTime.SpecifyKind(item.CurrentPeriodEnd, DateTimeKind.Utc));
        // Do not start a plan mutation across a renewal boundary while its invoice is forming.
        if (renewal <= now.AddMinutes(1)) return Fail("renewing");
        var next = await stripe.PreviewAsync(service.SubscriptionId, item.Id, priceId, now, true, ct);
        var due = direction == PlanChangeDirection.Upgrade
            ? await stripe.PreviewAsync(service.SubscriptionId, item.Id, priceId, now, false, ct) : null;
        if (next.Currency != subscription.Currency || (due is not null && due.Currency != subscription.Currency)) return Fail("invalid");
        var change = new SubscriptionPlanChange
        {
            Id = Guid.NewGuid(), HostingServiceId = service.Id, TargetPlanId = target.Plan.Id,
            StripeSubscriptionId = service.SubscriptionId, StripeCustomerId = service.CustomerId,
            SubscriptionItemId = item.Id, SourcePriceId = item.Price.Id, TargetPriceId = priceId,
            BaselineInvoiceId = latest.Id, Currency = subscription.Currency, BillingPeriod = service.BillingPeriod,
            DueNowMinor = due?.AmountDue ?? 0, NextInvoiceMinor = next.AmountDue,
            MemoryMb = target.Plan.RamMb, CpuPercent = target.Plan.CpuPercent, DiskMb = target.Plan.DiskMb,
            BackupLimit = target.Plan.BackupLimit, DatabaseLimit = target.Plan.DatabaseLimit, AllocationLimit = target.Plan.AllocationLimit,
            Direction = direction.Value, State = PlanChangeState.Quoted, ProrationAt = now, EffectiveAt = renewal,
            QuoteExpiresAt = now.AddMinutes(Math.Clamp(settings.Value.QuoteMinutes, 1, 10)), CreatedAt = now, UpdatedAt = now
        };
        // Native AmountDue includes Stripe credits, taxes, discounts and minor-unit rounding.
        return await store.AddChangeAsync(change, ct) ? new(true, "quoted", change) : Fail("pending");
    }

    public async Task<SubscriptionQuoteResult> ConfirmAsync(string owner, Guid changeId, CancellationToken ct)
    {
        if (!Enabled) return Fail("disabled");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(2)); // Strictly shorter than the durable five-minute lease.
        ct = budget.Token;
        var original = await store.FindChangeAsync(changeId, ct);
        if (original is null || await OwnedAsync(owner, original.HostingServiceId, ct) is null) return Fail("invalid");
        var change = await store.ClaimAsync(changeId, clock.GetUtcNow(), ct);
        if (change is null) return Fail("pending");
        var lease = change.LeaseToken!.Value;
        try
        {
            if (change.State != PlanChangeState.Quoted) return new(true, "pending", change);
            if (change.QuoteExpiresAt <= clock.GetUtcNow()) { Finish(change, PlanChangeState.Cancelled, "expired"); return Fail("expired"); }
            if (change.EffectiveAt <= clock.GetUtcNow().AddMinutes(1)) { Finish(change, PlanChangeState.Cancelled, "renewing"); return Fail("renewing"); }
            var service = (await OwnedAsync(owner, change.HostingServiceId, ct))!;
            var subscription = await stripe.ReadSubscriptionAsync(change.StripeSubscriptionId, ct);
            var item = ValidSubscription(service, subscription);
            if (!Unchanged(change, subscription, item) || subscription.PendingUpdate is not null || subscription.ScheduleId is not null)
            { Finish(change, PlanChangeState.Cancelled, "stale"); return Fail("stale"); }
            // Re-preview the frozen proration timestamp: no browser-submitted amount is accepted.
            if (change.Direction == PlanChangeDirection.Upgrade)
            {
                var quote = await stripe.PreviewAsync(change.StripeSubscriptionId, change.SubscriptionItemId,
                    change.TargetPriceId, change.ProrationAt, false, ct);
                if (quote.AmountDue != change.DueNowMinor || quote.Currency != change.Currency)
                { Finish(change, PlanChangeState.Cancelled, "stale"); return Fail("stale"); }
                if (!await resources.CanUpgradeAsync(service, PlanChangeRules.Limits(change), ct))
                { Finish(change, PlanChangeState.Cancelled, "capacity"); return Fail("capacity"); }
            }
            change.State = PlanChangeState.Submitting;
            change.UpdatedAt = clock.GetUtcNow();
            // Persist intent BEFORE crossing a financial boundary. A crash is safely recoverable.
            if (!await store.SaveAsync(change, lease, ct)) return Fail("pending");
            await SubmitAsync(change, ct);
            return new(true, "pending", change);
        }
        finally { await ReleaseLeaseAsync(change, lease, ct); }
    }

    public async Task<bool> CancelScheduledAsync(string owner, Guid id, CancellationToken ct)
    {
        if (!settings.Value.ReconciliationEnabled || !stripe.IsConfigured) return false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        ct = budget.Token;
        var record = await store.FindChangeAsync(id, ct);
        if (record is null || await OwnedAsync(owner, record.HostingServiceId, ct) is null) return false;
        var change = await store.ClaimAsync(id, clock.GetUtcNow(), ct);
        if (change is null) return false;
        var lease = change.LeaseToken!.Value;
        try
        {
            if (change.State == PlanChangeState.Quoted) { Finish(change, PlanChangeState.Cancelled); return true; }
            if (change.State != PlanChangeState.Scheduled || change.EffectiveAt <= clock.GetUtcNow()) return false;
            var sub = await stripe.ReadSubscriptionAsync(change.StripeSubscriptionId, ct);
            if (sub.CustomerId != change.StripeCustomerId || sub.Items?.Data is not [var item] || item.Price.Id != change.SourcePriceId)
                return false;
            change.CancellationRequested = true;
            if (!await store.SaveAsync(change, lease, ct)) return false;
            await stripe.ReleaseAsync(change, ct);
            Finish(change, PlanChangeState.Cancelled);
            return true;
        }
        finally { await ReleaseLeaseAsync(change, lease, ct); }
    }

    public async Task ReconcileAsync(string? subscriptionId, CancellationToken ct)
    {
        if (!settings.Value.ReconciliationEnabled || !stripe.IsConfigured) return;
        foreach (var id in await store.ListDueAsync(clock.GetUtcNow(), ct))
        {
            var original = await store.FindChangeAsync(id, ct);
            if (original is null || (subscriptionId is not null && original.StripeSubscriptionId != subscriptionId)) continue;
            var change = await store.ClaimAsync(id, clock.GetUtcNow(), ct);
            if (change is null) continue;
            var lease = change.LeaseToken!.Value;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromMinutes(2));
            try { await ReconcileOneAsync(change, budget.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Raw Stripe/panel responses can carry personal data and must not reach logs.
                logger.LogWarning("Subscription reconciliation will retry ({FailureClass}).", e.GetType().Name);
            }
            finally { await ReleaseLeaseAsync(change, lease, ct); }
        }
    }

    private async Task ReconcileOneAsync(SubscriptionPlanChange change, CancellationToken ct)
    {
        if (change.State == PlanChangeState.Quoted)
        { if (change.QuoteExpiresAt <= clock.GetUtcNow()) Finish(change, PlanChangeState.Cancelled, "expired"); return; }
        var service = await store.FindServiceAsync(change.HostingServiceId, ct);
        if (service is null || service.SubscriptionId != change.StripeSubscriptionId || service.CustomerId != change.StripeCustomerId)
        { Finish(change, PlanChangeState.Operational, "identity"); return; }
        // Re-read Stripe even when a signed event arrived: old/duplicate events are not current state.
        var sub = await stripe.ReadSubscriptionAsync(change.StripeSubscriptionId, ct);
        var item = ValidSubscription(service, sub);
        if (item is null) return; // Past-due/unpaid/cancelled subscriptions never change resources.
        if (change.CancellationRequested)
        {
            if (item.Price.Id != change.SourcePriceId || clock.GetUtcNow() >= change.EffectiveAt)
            { Finish(change, PlanChangeState.Operational, "cancellation-boundary"); return; }
            await stripe.ReleaseAsync(change, ct); Finish(change, PlanChangeState.Cancelled); return;
        }
        if (item.Price.Id == change.TargetPriceId && sub.PendingUpdate is null && sub.LatestInvoiceId is { } invoiceId)
        {
            var invoice = await stripe.ReadInvoiceAsync(invoiceId, ct);
            var line = invoice.Lines?.Data.FirstOrDefault(x => x.Pricing?.PriceDetails?.PriceId == change.TargetPriceId
                && (change.Direction == PlanChangeDirection.Upgrade || x.Period?.Start >= change.EffectiveAt.UtcDateTime));
            if (invoice.Id == change.BaselineInvoiceId || line is null || !Paid(invoice, service, change.Currency)) return;
            if (change.StripeInvoiceId is { } expected && change.Direction == PlanChangeDirection.Upgrade && expected != invoice.Id)
            { Finish(change, PlanChangeState.Operational, "invoice-changed"); return; }
            change.StripeInvoiceId = invoice.Id;
            change.State = PlanChangeState.Applying;
            if (!await store.SaveAsync(change, change.LeaseToken!.Value, ct)) return;
            // Apply is an idempotent build update of the same UUID, not provisioning.
            try { await resources.ApplyAsync(service, change, ct); }
            catch (UnsafeResourceReductionException) { Finish(change, PlanChangeState.Operational, "disk-usage-unverified"); return; }
            await store.SetAppliedPlanAsync(service.Id, change.TargetPlanId, ct);
            Finish(change, PlanChangeState.Applied);
            return;
        }
        if (change.State == PlanChangeState.Submitting)
        {
            if (change.CreatedAt <= clock.GetUtcNow().AddHours(-23))
            { Finish(change, PlanChangeState.Operational, "idempotency-window"); return; }
            if (change.Direction == PlanChangeDirection.Downgrade)
            {
                if (change.EffectiveAt <= clock.GetUtcNow() || item.Price.Id != change.SourcePriceId)
                { Finish(change, PlanChangeState.Operational, "schedule-boundary"); return; }
                await SubmitAsync(change, ct);
                return;
            }
            // Pending invoices are evidence an update already ran; never create a second charge.
            if (sub.LatestInvoiceId != change.BaselineInvoiceId && sub.LatestInvoiceId is { } pendingId)
            {
                change.StripeInvoiceId = pendingId;
                change.State = PlanChangeState.AwaitingPayment;
                return;
            }
            if (item.Price.Id != change.SourcePriceId)
            { Finish(change, PlanChangeState.Operational, "price-changed"); return; }
            await SubmitAsync(change, ct);
        }
        if (change.State == PlanChangeState.AwaitingPayment && sub.PendingUpdate is null && item.Price.Id == change.SourcePriceId
            && change.StripeInvoiceId is { } failedId)
        {
            var invoice = await stripe.ReadInvoiceAsync(failedId, ct);
            if (invoice.Status is "void" or "uncollectible") Finish(change, PlanChangeState.Failed, "payment-failed");
        }
        if (change.State == PlanChangeState.AwaitingPayment && change.StripeInvoiceId is null
            && change.CreatedAt < clock.GetUtcNow().AddHours(-2))
            Finish(change, PlanChangeState.Operational, "missing-invoice");
    }

    private async Task SubmitAsync(SubscriptionPlanChange change, CancellationToken ct)
    {
        if (change.Direction == PlanChangeDirection.Upgrade)
        {
            var sub = await stripe.UpgradeAsync(change, ct);
            if (sub.Id != change.StripeSubscriptionId || sub.CustomerId != change.StripeCustomerId)
                throw new InvalidOperationException("Subscription identity mismatch.");
            change.StripeInvoiceId = sub.LatestInvoiceId != change.BaselineInvoiceId ? sub.LatestInvoiceId : null;
            change.State = PlanChangeState.AwaitingPayment;
        }
        else
        {
            var schedule = await stripe.ScheduleAsync(change, ct);
            change.StripeScheduleId = schedule.Id;
            change.State = PlanChangeState.Scheduled;
        }
    }

    private async Task<ManagedService?> OwnedAsync(string owner, Guid id, CancellationToken ct)
    {
        var service = await store.FindServiceAsync(id, ct);
        return service is not null && service.ServerId > 0 && !string.IsNullOrEmpty(service.ServerUuid)
            && string.Equals(service.OwnerEmail.Trim(), owner, StringComparison.OrdinalIgnoreCase) ? service : null;
    }
    private static SubscriptionItem? ValidSubscription(ManagedService service, Subscription sub) =>
        sub.Id == service.SubscriptionId && sub.CustomerId == service.CustomerId && sub.Status == "active"
        && sub.CollectionMethod == "charge_automatically" && sub.Items?.Data is [var item] && item.Quantity == 1 ? item : null;
    private static bool Unchanged(SubscriptionPlanChange c, Subscription sub, SubscriptionItem? item) =>
        item is not null && item.Id == c.SubscriptionItemId && item.Price.Id == c.SourcePriceId
        && item.CurrentPeriodEnd == c.EffectiveAt.UtcDateTime && sub.LatestInvoiceId == c.BaselineInvoiceId && !sub.CancelAtPeriodEnd;
    private static bool ValidPrice(Price p, string period, string currency) => p.Active && p.Type == "recurring"
        && p.Currency == currency && p.UnitAmount is > 0 && p.BillingScheme == "per_unit" && p.Recurring is { UsageType: "licensed" } r
        && PlanChangeRules.SameInterval(period, r.Interval, r.IntervalCount);
    private static bool Paid(Invoice invoice, ManagedService s, string currency) => invoice.CustomerId == s.CustomerId
        && invoice.Parent?.SubscriptionDetails?.SubscriptionId == s.SubscriptionId
        && invoice.Currency == currency && invoice.Status == "paid" && invoice.AmountRemaining == 0;
    private static SubscriptionQuoteResult Fail(string code) => new(false, code);
    private static void Finish(SubscriptionPlanChange c, PlanChangeState state, string? code = null)
    {
        c.State = state; c.FailureCode = code;
        // Operational keeps the exclusivity fence until staff safely resolves the change.
        c.Active = state == PlanChangeState.Operational;
    }
    private async Task ReleaseLeaseAsync(SubscriptionPlanChange c, Guid lease, CancellationToken ct)
    {
        c.UpdatedAt = clock.GetUtcNow(); c.LeaseToken = null; c.LeaseExpiresAt = null;
        await store.SaveAsync(c, lease, ct);
    }
}

public sealed class UnsafeResourceReductionException : Exception { }
