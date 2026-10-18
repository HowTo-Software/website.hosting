using global::Stripe;
using HowToSoftware.Hosting.Models.Commerce;

namespace HowToSoftware.Hosting.Infrastructure.Stripe;

public interface ISubscriptionGateway
{
    bool IsConfigured { get; }
    Task<Subscription> ReadSubscriptionAsync(string id, CancellationToken ct);
    Task<Price> ReadPriceAsync(string id, CancellationToken ct);
    Task<Invoice> PreviewAsync(string subscription, string item, string price, DateTimeOffset at, bool recurring, CancellationToken ct);
    Task<Invoice> UpcomingAsync(string subscription, CancellationToken ct);
    Task<Invoice> ReadInvoiceAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<Invoice>> InvoicesAsync(string customer, string subscription, CancellationToken ct);
    Task<Subscription> UpgradeAsync(SubscriptionPlanChange change, CancellationToken ct);
    Task<SubscriptionSchedule> ScheduleAsync(SubscriptionPlanChange change, CancellationToken ct);
    Task ReleaseAsync(SubscriptionPlanChange change, CancellationToken ct);
}

public sealed partial class StripeGateway
{
    public Task<Subscription> ReadSubscriptionAsync(string id, CancellationToken ct) =>
        Client().V1.Subscriptions.GetAsync(id, new SubscriptionGetOptions { Expand = ["latest_invoice"] }, cancellationToken: ct);

    public Task<Price> ReadPriceAsync(string id, CancellationToken ct) => Client().V1.Prices.GetAsync(id, cancellationToken: ct);

    public Task<Invoice> PreviewAsync(string subscription, string item, string price, DateTimeOffset at, bool recurring, CancellationToken ct) =>
        Client().V1.Invoices.CreatePreviewAsync(new InvoiceCreatePreviewOptions
        {
            Subscription = subscription,
            PreviewMode = recurring ? "recurring" : "next",
            SubscriptionDetails = new InvoiceSubscriptionDetailsOptions
            {
                Items = [new InvoiceSubscriptionDetailsItemOptions { Id = item, Price = price, Quantity = 1 }],
                ProrationBehavior = recurring ? "none" : "always_invoice",
                ProrationDate = recurring ? null : at.UtcDateTime
            }
        }, cancellationToken: ct);

    public Task<Invoice> UpcomingAsync(string subscription, CancellationToken ct) =>
        Client().V1.Invoices.CreatePreviewAsync(new InvoiceCreatePreviewOptions { Subscription = subscription }, cancellationToken: ct);

    public async Task<Invoice> ReadInvoiceAsync(string id, CancellationToken ct)
    {
        var client = Client();
        var invoice = await client.V1.Invoices.GetAsync(id, cancellationToken: ct);
        if (invoice.Lines?.HasMore == true)
        {
            var lines = new List<InvoiceLineItem>();
            await foreach (var line in new InvoiceLineItemService(client).ListAutoPagingAsync(id,
                new InvoiceLineItemListOptions { Limit = 100 }, cancellationToken: ct)) lines.Add(line);
            invoice.Lines = new StripeList<InvoiceLineItem> { Data = lines };
        }
        return invoice;
    }

    public async Task<IReadOnlyList<Invoice>> InvoicesAsync(string customer, string subscription, CancellationToken ct) =>
        (await Client().V1.Invoices.ListAsync(new InvoiceListOptions
        { Customer = customer, Subscription = subscription, Limit = 25 }, cancellationToken: ct)).Data;

    public Task<Subscription> UpgradeAsync(SubscriptionPlanChange change, CancellationToken ct) =>
        Client().V1.Subscriptions.UpdateAsync(change.StripeSubscriptionId, new SubscriptionUpdateOptions
        {
            Items = [new SubscriptionItemOptions { Id = change.SubscriptionItemId, Price = change.TargetPriceId, Quantity = 1 }],
            ProrationBehavior = "always_invoice",
            PaymentBehavior = "pending_if_incomplete",
            ProrationDate = change.ProrationAt.UtcDateTime
        }, new RequestOptions { IdempotencyKey = $"hts-change:{change.Id:N}:upgrade" }, ct);

    public async Task<SubscriptionSchedule> ScheduleAsync(SubscriptionPlanChange change, CancellationToken ct)
    {
        var client = Client();
        var schedule = change.StripeScheduleId is { } existing
            ? await client.V1.SubscriptionSchedules.GetAsync(existing, cancellationToken: ct)
            : await client.V1.SubscriptionSchedules.CreateAsync(new SubscriptionScheduleCreateOptions
            { FromSubscription = change.StripeSubscriptionId },
                new RequestOptions { IdempotencyKey = $"hts-change:{change.Id:N}:schedule-create" }, ct);
        if (schedule.SubscriptionId == change.StripeSubscriptionId && schedule.CustomerId == change.StripeCustomerId
            && schedule.Metadata?.GetValueOrDefault("hts_plan_change") == change.Id.ToString("N")
            && schedule.Phases is [var previous, var future]
            && previous.EndDate == change.EffectiveAt.UtcDateTime
            && future.StartDate == change.EffectiveAt.UtcDateTime
            && future.Items is [var futureItem] && futureItem.PriceId == change.TargetPriceId)
            return schedule; // Recover an update that succeeded before the local acknowledgement.
        if (schedule.SubscriptionId != change.StripeSubscriptionId || schedule.CustomerId != change.StripeCustomerId
            || schedule.Phases is not [var phase] || phase.Items.Count != 1
            || phase.Items[0].PriceId != change.SourcePriceId || phase.Items[0].Quantity != 1
            || phase.EndDate != change.EffectiveAt.UtcDateTime
            || phase.AddInvoiceItems?.Count > 0 || phase.TransferData is not null
            || phase.ApplicationFeePercent is not null || phase.OnBehalfOfId is not null
            || phase.BillingThresholds is not null)
            throw new InvalidOperationException("The subscription schedule requires operational review.");

        SubscriptionSchedulePhaseOptions Phase(string price) => new()
        {
            Items = [new SubscriptionSchedulePhaseItemOptions
            {
                Price = price, Quantity = 1,
                TaxRates = phase.Items[0].TaxRates?.Select(x => x.Id).ToList(),
                Discounts = phase.Items[0].Discounts?.Select(x => new SubscriptionSchedulePhaseItemDiscountOptions
                { Discount = x.DiscountId, Coupon = x.DiscountId is null && x.PromotionCodeId is null ? x.CouponId : null, PromotionCode = x.DiscountId is null ? x.PromotionCodeId : null }).ToList()
            }],
            AutomaticTax = phase.AutomaticTax is null ? null : new()
            {
                Enabled = phase.AutomaticTax.Enabled,
                Liability = phase.AutomaticTax.Liability is null ? null : new()
                { Type = phase.AutomaticTax.Liability.Type, Account = phase.AutomaticTax.Liability.AccountId }
            },
            InvoiceSettings = phase.InvoiceSettings is null ? null : new()
            {
                AccountTaxIds = phase.InvoiceSettings.AccountTaxIdIds,
                CustomFields = phase.InvoiceSettings.CustomFields?.Select(x => new SubscriptionSchedulePhaseInvoiceSettingsCustomFieldOptions { Name = x.Name, Value = x.Value }).ToList(),
                DaysUntilDue = phase.InvoiceSettings.DaysUntilDue, Description = phase.InvoiceSettings.Description,
                Footer = phase.InvoiceSettings.Footer,
                Issuer = phase.InvoiceSettings.Issuer is null ? null : new()
                { Type = phase.InvoiceSettings.Issuer.Type, Account = phase.InvoiceSettings.Issuer.AccountId }
            },
            Metadata = phase.Metadata,
            DefaultTaxRates = phase.DefaultTaxRates?.Select(x => x.Id).ToList(),
            DefaultPaymentMethod = phase.DefaultPaymentMethodId,
            Discounts = phase.Discounts?.Select(x => new SubscriptionSchedulePhaseDiscountOptions
            { Discount = x.DiscountId, Coupon = x.DiscountId is null && x.PromotionCodeId is null ? x.CouponId : null, PromotionCode = x.DiscountId is null ? x.PromotionCodeId : null }).ToList(),
            ProrationBehavior = "none",
            CollectionMethod = phase.CollectionMethod,
            BillingCycleAnchor = "automatic"
        };
        var current = Phase(change.SourcePriceId);
        current.StartDate = phase.StartDate;
        current.EndDate = change.EffectiveAt.UtcDateTime;
        var next = Phase(change.TargetPriceId);
        next.StartDate = change.EffectiveAt.UtcDateTime;
        next.Duration = change.BillingPeriod switch
        {
            "annual" => new() { Interval = "year", IntervalCount = 1 },
            "quarterly" => new() { Interval = "month", IntervalCount = 3 },
            _ => new() { Interval = "month", IntervalCount = 1 }
        };
        return await client.V1.SubscriptionSchedules.UpdateAsync(schedule.Id, new SubscriptionScheduleUpdateOptions
        {
            Phases = [current, next], EndBehavior = "release", ProrationBehavior = "none",
            Metadata = new() { ["hts_plan_change"] = change.Id.ToString("N") }
        }, new RequestOptions { IdempotencyKey = $"hts-change:{change.Id:N}:schedule-update" }, ct);
    }

    public async Task ReleaseAsync(SubscriptionPlanChange change, CancellationToken ct)
    {
        var schedule = await Client().V1.SubscriptionSchedules.GetAsync(change.StripeScheduleId, cancellationToken: ct);
        if (schedule.CustomerId != change.StripeCustomerId
            || (schedule.SubscriptionId != change.StripeSubscriptionId && schedule.ReleasedSubscription != change.StripeSubscriptionId)
            || schedule.Metadata?.GetValueOrDefault("hts_plan_change") != change.Id.ToString("N"))
            throw new InvalidOperationException("Schedule ownership did not match.");
        if (schedule.Status == "released") return;
        // Releasing preserves the live subscription. Cancelling a schedule cancels billing too.
        await Client().V1.SubscriptionSchedules.ReleaseAsync(schedule.Id,
            requestOptions: new RequestOptions { IdempotencyKey = $"hts-change:{change.Id:N}:release" }, cancellationToken: ct);
    }
}
