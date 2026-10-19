using System.Globalization;
using global::Stripe;
using global::Stripe.Checkout;
using HowToSoftware.Hosting.Infrastructure.Stripe;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Orders;
using HowToSoftware.Hosting.Services.Orders;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;

namespace HowToSoftware.Hosting.Services.Payments;

/// <summary>
/// What the review page sends when the customer continues to payment. Note what is absent: a
/// price. The server works that out.
/// </summary>
/// <param name="GameSlug">The game.</param>
/// <param name="PlanSlug">The plan.</param>
/// <param name="Period">The billing period.</param>
/// <param name="ReturnOrigin">
/// Server-configured public origin (e.g. <c>https://howtoosoftware.com</c>), used to build the
/// return URLs when none are configured. It must never be copied from a request Host header.
/// </param>
/// <param name="UserId">The authenticated user, when there is one.</param>
/// <param name="Locale">The customer's culture, so Checkout speaks the same language as the site.</param>
public sealed record CheckoutRequest(
    string GameSlug,
    string PlanSlug,
    BillingPeriod Period,
    string ReturnOrigin,
    string? UserId,
    string? Locale,
    string? PromoCode,
    string? TrialAccessToken = null,
    bool IsTrialUpgrade = false,
    string? Currency = null);

/// <summary>How an attempt to start Checkout ended.</summary>
public enum CheckoutOutcome
{
    /// <summary>A session exists; send the customer to <see cref="CheckoutStart.RedirectUrl"/>.</summary>
    Redirect,

    /// <summary>No Stripe secret key on this host. The button says so instead of failing.</summary>
    NotConfigured,

    /// <summary>The game, plan or period did not resolve to something for sale.</summary>
    Rejected,

    /// <summary>Stripe declined to create the session.</summary>
    Failed
}

/// <summary>Result of starting Checkout.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="OrderId">The order created, when one was.</param>
/// <param name="RedirectUrl">The Stripe-hosted Checkout page, on success.</param>
public sealed record CheckoutStart(CheckoutOutcome Outcome, Guid? OrderId, string? RedirectUrl)
{
    /// <summary>Whether the customer can be sent on to Stripe.</summary>
    public bool IsRedirect => Outcome is CheckoutOutcome.Redirect && RedirectUrl is not null;
}

/// <summary>
/// The application's side of the Stripe integration.
/// </summary>
/// <remarks>
/// Nothing in a Razor component talks to Stripe. The review page calls
/// <see cref="CreateCheckoutSessionAsync"/>; the webhook calls the two handlers; the success
/// page calls <see cref="GetCheckoutSessionAsync"/>. The price check is public so the webhook
/// handler and its tests can call it directly.
/// </remarks>
public interface IStripeCheckoutService
{
    /// <summary>Whether Checkout sessions can be created on this host.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Prices the request on the server, records a pending order, and creates a Stripe Checkout
    /// Session for it.
    /// </summary>
    Task<CheckoutStart> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a Checkout Session from Stripe, or <see langword="null"/> when it cannot be.</summary>
    Task<Session?> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acts on a completed Checkout Session delivered by a verified webhook: checks the amount,
    /// records the customer and subscription, marks the order paid and queues fulfilment.
    /// </summary>
    Task HandleCompletedCheckoutAsync(Session session, CancellationToken cancellationToken = default);

    /// <summary>Acts on an expired Checkout Session: the order is cancelled, its configuration kept.</summary>
    Task HandleExpiredCheckoutAsync(Session session, CancellationToken cancellationToken = default);

    /// <summary>Mirrors a subscription's Stripe status onto its order.</summary>
    Task HandleSubscriptionStateAsync(Subscription subscription, CancellationToken cancellationToken = default);

    /// <summary>Mirrors Stripe invoice state and keeps a small invoice reference cache.</summary>
    Task HandleInvoiceStateAsync(Invoice invoice, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether what Stripe reports as paid is exactly what the server told it to charge.
    /// </summary>
    /// <param name="order">The order, carrying the amount the server computed.</param>
    /// <param name="amountTotalMinor">Stripe's <c>amount_total</c>, in the minor unit.</param>
    /// <param name="currency">Stripe's currency code.</param>
    bool ValidateOrderPrice(Order order, long? amountTotalMinor, string? currency);
}

/// <summary>
/// <see cref="IStripeCheckoutService"/> over <see cref="IStripeGateway"/>.
/// </summary>
public sealed class StripeCheckoutService : IStripeCheckoutService
{
    /// <summary>Metadata keys, identical on the session and the subscription it creates.</summary>
    public static class MetadataKeys
    {
        public const string OrderId = "internal_order_id";
        public const string UserId = "user_id";
        public const string GameId = "game_id";
        public const string PlanId = "plan_id";
        public const string BillingPeriod = "billing_period";
        public const string PromotionCodeId = "promotion_code_id";
        public const string PromotionDiscountCents = "promotion_discount_cents";
    }

    private readonly IStripeGateway _stripe;
    private readonly IOrderPricingService _pricing;
    private readonly IPromotionService _promotions;
    private readonly IOrderStore _orders;
    private readonly OrderFulfillmentQueue _fulfilment;
    private readonly IOptionsMonitor<StripeOptions> _options;
    private readonly IBillingStore _billing;
    private readonly TimeProvider _clock;
    private readonly ITrialService? _trials;
    private readonly ILogger<StripeCheckoutService> _logger;

    /// <summary>Creates the service.</summary>
    public StripeCheckoutService(
        IStripeGateway stripe,
        IOrderPricingService pricing,
        IPromotionService promotions,
        IOrderStore orders,
        OrderFulfillmentQueue fulfilment,
        IOptionsMonitor<StripeOptions> options,
        TimeProvider clock,
        ILogger<StripeCheckoutService> logger,
        IBillingStore? billing = null,
        ITrialService? trials = null)
    {
        _stripe = stripe;
        _pricing = pricing;
        _promotions = promotions;
        _orders = orders;
        _fulfilment = fulfilment;
        _options = options;
        _billing = billing ?? new NullBillingStore();
        _trials = trials;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => _stripe.IsConfigured && _orders.IsConfigured;

    /// <inheritdoc />
    public async Task<CheckoutStart> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsTrialUpgrade && string.IsNullOrWhiteSpace(request.TrialAccessToken))
            return new CheckoutStart(CheckoutOutcome.Rejected, null, null);

        if (!IsConfigured)
        {
            return new CheckoutStart(CheckoutOutcome.NotConfigured, null, null);
        }

        var pixSettings = _options.CurrentValue.PixSubscriptions;
        if (pixSettings.Enabled && (!StripePixSubscriptionsOptions.IsEligibleAccountCountry(pixSettings.AccountCountry)
            || pixSettings.AmountIncludesIof is not ("always" or "never")))
        {
            _logger.LogWarning("Checkout unavailable: Pix subscription eligibility and IOF policy require valid configuration.");
            return new CheckoutStart(CheckoutOutcome.NotConfigured, null, null);
        }

        // The server prices the order. Whatever the browser thought the plan cost is not an
        // input to anything below this line.
        if (_pricing.Price(request.GameSlug, request.PlanSlug, request.Period, request.Currency) is not { } priced)
        {
            _logger.LogWarning(
                "Checkout rejected: {Game}/{Plan}/{Period} does not resolve to a priced plan.",
                request.GameSlug, request.PlanSlug, request.Period);

            return new CheckoutStart(CheckoutOutcome.Rejected, null, null);
        }

        PromotionResult? promotion = null;

        if (!string.IsNullOrWhiteSpace(request.PromoCode))
        {
            promotion = await _promotions.ValidateAsync(
                request.PromoCode,
                priced.Game.Slug,
                priced.Plan.Slug,
                priced.Quote,
                cancellationToken, priced.CurrencyCode).ConfigureAwait(false);

            if (promotion is null)
            {
                // The code itself is caller-supplied, so it is deliberately left out of the log.
                _logger.LogInformation(
                    "Checkout rejected: the promotion code supplied for {Game}/{Plan} is invalid or not applicable.",
                    priced.Game.Slug,
                    priced.Plan.Slug);

                return new CheckoutStart(CheckoutOutcome.Rejected, null, null);
            }
        }

        if (!TryNormaliseReturnOrigin(request.ReturnOrigin, out var returnOrigin))
        {
            _logger.LogWarning("Checkout rejected because the configured public origin is not a secure origin.");
            return new CheckoutStart(CheckoutOutcome.Rejected, null, null);
        }

        request = request with { ReturnOrigin = returnOrigin };

        TrialAccountView? trial = null;
        if (!string.IsNullOrWhiteSpace(request.TrialAccessToken))
        {
            trial = _trials is null ? null : await _trials.GetByAccessTokenAsync(request.TrialAccessToken, cancellationToken).ConfigureAwait(false);
            if (trial is null || !trial.CanUpgrade || !string.Equals(trial.GameSlug, priced.Game.Slug, StringComparison.Ordinal))
                return new CheckoutStart(CheckoutOutcome.Rejected, null, null);
            request = request with { UserId = CustomerIdentity.FromEmail(trial.Email).ToString("D") };
        }

        // A Minecraft purchase converts an existing, verified trial and keeps its chosen egg.
        if (priced.Game.Slug == StaticGameCatalogService.MinecraftSlug && trial is null)
            return new CheckoutStart(CheckoutOutcome.Rejected, null, null);

        var now = _clock.GetUtcNow();
        var existingStripeCustomerId = string.IsNullOrWhiteSpace(request.UserId)
            ? null
            : await _billing.FindStripeCustomerIdAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var finalAmount = promotion?.FinalAmount ?? priced.Quote.FinalAmount;
        var totalDiscountAmount =
            (priced.Quote.BaseAmount - finalAmount);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            CustomerEmail = trial?.Email,
            GameId = priced.Game.Slug,
            PlanId = priced.Plan.Slug,
            PlanName = priced.Plan.Name,
            BillingPeriod = priced.Quote.Period,
            MonthlyPrice = priced.Quote.MonthlyPrice,
            BaseAmount = priced.Quote.BaseAmount,
            DiscountPercentage = priced.Quote.DiscountPercent,
            DiscountAmount = totalDiscountAmount,
            FinalAmount = finalAmount,
            Currency = priced.CurrencyCode,
            Status = OrderStatus.Pending,
            ProvisioningStage = FulfilmentStage.NotStarted,
            StripeCustomerId = existingStripeCustomerId,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            await _orders.AddAsync(order, cancellationToken).ConfigureAwait(false);
            if (trial is not null)
            {
                var reservation = await _trials!.ReserveUpgradeAsync(trial.Id, request.TrialAccessToken!, order.Id, cancellationToken).ConfigureAwait(false);
                if (!reservation.Reserved)
                {
                    order.TransitionTo(OrderStatus.Cancelled, _clock.GetUtcNow());
                    await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
                    return new CheckoutStart(CheckoutOutcome.Rejected, order.Id, null);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // No Stripe call has happened. A store outage must not turn a reservation into
            // payment for a fresh server or expose SQL details in the review response.
            _logger.LogWarning("Order {OrderId}: checkout persistence failed ({FailureClass}).", order.Id, error.GetType().Name);
            try
            {
                order.TransitionTo(OrderStatus.Failed, _clock.GetUtcNow());
                order.FailureReason = "Checkout persistence unavailable before payment.";
                await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
                if (trial is not null) await _trials!.ReleaseUpgradeAsync(order.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (cleanupError is not OperationCanceledException)
            {
                _logger.LogWarning("Order {OrderId}: pending checkout cleanup deferred ({FailureClass}).", order.Id, cleanupError.GetType().Name);
            }
            return new CheckoutStart(CheckoutOutcome.Failed, order.Id, null);
        }

        var sessionOptions = BuildSessionOptions(order, priced, request, promotion);
        if (trial is not null) sessionOptions.ExpiresAt = _clock.GetUtcNow().AddMinutes(30).UtcDateTime;

        try
        {
            var session = await _stripe.CreateCheckoutSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);

            order.StripeCheckoutSessionId = session.Id;
            order.UpdatedAt = _clock.GetUtcNow();
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Order {OrderId}: Checkout session created for {Plan}/{Period} at {Amount} {Currency}.",
                order.Id, order.PlanId, order.BillingPeriod, order.FinalAmount, order.Currency);

            return new CheckoutStart(CheckoutOutcome.Redirect, order.Id, session.Url);
        }
        catch (StripeException exception)
        {
            // The exception message describes the request Stripe rejected; it never carries a
            // key. Logged as the type and Stripe's own error code, which is what an operator
            // needs and nothing a customer would.
            order.TransitionTo(OrderStatus.Failed, _clock.GetUtcNow());
            if (trial is not null) await _trials!.ReleaseUpgradeAsync(order.Id, cancellationToken).ConfigureAwait(false);
            order.FailureReason = $"Stripe rejected the Checkout session ({exception.StripeError?.Code ?? exception.GetType().Name}).";
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

            _logger.LogError(
                exception,
                "Order {OrderId}: Stripe refused to create a Checkout session (code {Code}).",
                order.Id, exception.StripeError?.Code);

            return new CheckoutStart(CheckoutOutcome.Failed, order.Id, null);
        }
    }

    /// <inheritdoc />
    public async Task<Session?> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_stripe.IsConfigured || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        try
        {
            return await _stripe.GetCheckoutSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (StripeException exception)
        {
            _logger.LogWarning("Checkout session lookup failed (code {Code}).", exception.StripeError?.Code);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task HandleCompletedCheckoutAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var order = await FindOrderForSessionAsync(session, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            _logger.LogWarning("checkout.session.completed for an unknown session; nothing to fulfil.");
            return;
        }

        // Delayed payment methods complete the session before the money arrives. Stripe sends
        // checkout.session.async_payment_succeeded later; until then the order stays pending.
        if (!string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Order {OrderId}: session completed with payment_status={Status}; waiting for payment.",
                order.Id, session.PaymentStatus);
            return;
        }

        var now = _clock.GetUtcNow();

        if (!ValidateOrderPrice(order, session.AmountTotal, session.Currency))
        {
            if (order.CanTransitionTo(OrderStatus.Failed))
            {
                order.TransitionTo(OrderStatus.Failed, now);
            }

            order.FailureReason =
                $"Paid amount {session.AmountTotal} {session.Currency} does not match the order's {order.FinalAmountMinorUnits()} {order.Currency}.";
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

            _logger.LogError(
                "Order {OrderId}: amount mismatch - Stripe reports {Paid} {PaidCurrency}, order expects {Expected} {Currency}. Not fulfilling.",
                order.Id, session.AmountTotal, session.Currency, order.FinalAmountMinorUnits(), order.Currency);
            return;
        }

        var paid = await _orders.TryMarkPaidAsync(
            orderId: order.Id,
            now: now,
            customerEmail: session.CustomerDetails?.Email ?? session.CustomerEmail,
            stripeCustomerId: session.CustomerId,
            stripePaymentIntentId: session.PaymentIntentId,
            stripeSubscriptionId: session.SubscriptionId,
            stripeCheckoutSessionId: session.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!paid)
        {
            var currentOrder = await _orders.FindAsync(order.Id, cancellationToken).ConfigureAwait(false);
            if (currentOrder?.PaidAt is not null)
            {
                await RedeemPromotionForPaidOrderAsync(currentOrder, session, now, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Order {OrderId} is already {Status}; completed session ignored.", order.Id, currentOrder?.Status ?? order.Status);
            return;
        }

        _fulfilment.Enqueue(order.Id);
        await RedeemPromotionForPaidOrderAsync(order, session, now, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Order {OrderId}: paid. Queued for provisioning.", order.Id);
    }

    /// <inheritdoc />
    public async Task HandleExpiredCheckoutAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var order = await FindOrderForSessionAsync(session, cancellationToken).ConfigureAwait(false);

        if (order is null || !order.CanTransitionTo(OrderStatus.Cancelled))
        {
            return;
        }

        // Cancelled, not deleted: the plan and period are kept so the cancel page can offer
        // to try again with the same configuration.
        order.TransitionTo(OrderStatus.Cancelled, _clock.GetUtcNow());
        await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

        if (_trials is not null) await _trials.ReleaseUpgradeAsync(order.Id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Order {OrderId}: Checkout session expired; order cancelled.", order.Id);
    }

    /// <inheritdoc />
    public async Task HandleSubscriptionStateAsync(Subscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var order = await _orders.FindBySubscriptionAsync(subscription.Id, cancellationToken).ConfigureAwait(false);

        if (order is null
            && subscription.Metadata is { } metadata
            && metadata.TryGetValue(MetadataKeys.OrderId, out var raw)
            && Guid.TryParse(raw, out var orderId))
        {
            order = await _orders.FindAsync(orderId, cancellationToken).ConfigureAwait(false);
        }

        if (order is null)
        {
            _logger.LogWarning("Subscription event for an unknown subscription; ignored.");
            return;
        }

        order.StripeSubscriptionId ??= subscription.Id;
        order.SubscriptionStatus = subscription.Status;
        order.UpdatedAt = _clock.GetUtcNow();
        await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Order {OrderId}: subscription status is now {Status}.", order.Id, subscription.Status);
    }

    /// <inheritdoc />
    public async Task HandleInvoiceStateAsync(Invoice invoice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        Order? order = null;

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            order = await _orders.FindBySubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
        }

        if (order is null
            && invoice.Parent?.SubscriptionDetails?.Metadata is { } metadata
            && metadata.TryGetValue(MetadataKeys.OrderId, out var rawOrderId)
            && Guid.TryParse(rawOrderId, out var orderId))
        {
            order = await _orders.FindAsync(orderId, cancellationToken).ConfigureAwait(false);
        }

        if (order is null)
        {
            _logger.LogWarning("Invoice event for an unknown subscription; ignored.");
            return;
        }

        order.StripeInvoiceId = invoice.Id;
        order.StripeSubscriptionId ??= subscriptionId;
        order.UpdatedAt = _clock.GetUtcNow();
        await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        await _billing.SyncInvoiceAsync(order, invoice, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Order {OrderId}: invoice {InvoiceId} is {Status}.",
            order.Id,
            invoice.Id,
            invoice.Status);
    }

    /// <inheritdoc />
    public bool ValidateOrderPrice(Order order, long? amountTotalMinor, string? currency)
    {
        ArgumentNullException.ThrowIfNull(order);

        return amountTotalMinor is { } paid
            && paid == order.FinalAmountMinorUnits()
            && string.Equals(currency, order.Currency, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Order?> FindOrderForSessionAsync(Session session, CancellationToken cancellationToken)
    {
        var order = await _orders.FindByCheckoutSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);

        if (order is not null)
        {
            return order;
        }

        // The session id is written to the order as soon as Stripe returns it, but a webhook can
        // race that write. client_reference_id is set at creation and cannot race anything.
        return Guid.TryParse(session.ClientReferenceId, out var orderId)
            ? await _orders.FindAsync(orderId, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private SessionCreateOptions BuildSessionOptions(
        Order order,
        PricedPlan priced,
        CheckoutRequest request,
        PromotionResult? promotion)
    {
        var options = _options.CurrentValue;
        var periodSlug = BillingPolicy.Slug(order.BillingPeriod);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetadataKeys.OrderId] = order.Id.ToString("D"),
            [MetadataKeys.GameId] = order.GameId,
            [MetadataKeys.PlanId] = order.PlanId,
            [MetadataKeys.BillingPeriod] = periodSlug
        };

        if (promotion is not null)
        {
            metadata[MetadataKeys.PromotionCodeId] = promotion.Promotion.Id.ToString("D");
            metadata[MetadataKeys.PromotionDiscountCents] = ToMinorUnits(promotion.DiscountAmount).ToString(CultureInfo.InvariantCulture);
            metadata["promotion_code"] = promotion.Promotion.Code;
        }

        if (!string.IsNullOrWhiteSpace(order.UserId))
        {
            metadata[MetadataKeys.UserId] = order.UserId;
        }

        var lineItem = new SessionLineItemOptions { Quantity = 1 };

        if (options.FindPriceId(order.PlanId, periodSlug, order.Currency) is { } priceId)
        {
            // A Price created in the dashboard for this plan and period. The webhook still
            // checks the paid amount against the server's figure, so a dashboard price that
            // drifts from the rate card is caught rather than honoured.
            lineItem.Price = priceId;
        }
        else
        {
            lineItem.PriceData = new SessionLineItemPriceDataOptions
            {
                Currency = order.Currency,
                UnitAmount = checked((long)Math.Round(
                    order.FinalAmount * 100m,
                    MidpointRounding.AwayFromZero)),
                Recurring = new SessionLineItemPriceDataRecurringOptions
                {
                    Interval = "month",
                    IntervalCount = priced.Quote.Months
                },
                ProductData = new SessionLineItemPriceDataProductDataOptions
                {
                    Name = $"{priced.Game.Name} - {priced.Plan.Name}",
                    Description = DescribePlan(priced),
                    TaxCode = "txcd_10010001",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MetadataKeys.GameId] = order.GameId,
                        [MetadataKeys.PlanId] = order.PlanId
                    }
                }
            };
        }

        var successUrl = string.IsNullOrWhiteSpace(options.SuccessUrl)
            ? $"{request.ReturnOrigin.TrimEnd('/')}{SiteRoutes.PaymentSuccess}?session_id={{CHECKOUT_SESSION_ID}}"
            : options.SuccessUrl;

        var cancelUrl = (string.IsNullOrWhiteSpace(options.CancelUrl)
            ? $"{request.ReturnOrigin.TrimEnd('/')}{SiteRoutes.PaymentCancel}?game={{GAME_SLUG}}&plan={{PLAN_SLUG}}&period={{BILLING_PERIOD}}&currency={{CURRENCY}}"
            : options.CancelUrl)
            .Replace("{GAME_SLUG}", Uri.EscapeDataString(order.GameId), StringComparison.Ordinal)
            .Replace("{PLAN_SLUG}", Uri.EscapeDataString(order.PlanId), StringComparison.Ordinal)
            .Replace("{BILLING_PERIOD}", Uri.EscapeDataString(periodSlug), StringComparison.Ordinal)
            .Replace("{CURRENCY}", Uri.EscapeDataString(order.Currency), StringComparison.Ordinal);

        // Older configured URLs have no currency placeholder. Always round-trip the actual
        // order currency, replacing duplicate/stale query values without losing other fields.
        var cancelBuilder = new UriBuilder(cancelUrl);
        var cancelQuery = QueryHelpers.ParseQuery(cancelBuilder.Query);
        cancelQuery["currency"] = order.Currency;
        cancelBuilder.Query = QueryString.Create(cancelQuery).ToUriComponent();
        cancelUrl = cancelBuilder.Uri.AbsoluteUri;

        var pix = options.PixSubscriptions;
        var allowPix = pix.Enabled && order.Currency == "brl"
            && StripePixSubscriptionsOptions.IsEligibleAccountCountry(pix.AccountCountry)
            && pix.AmountIncludesIof is "always" or "never";

        return new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = order.StripeCustomerId,
            CustomerEmail = order.StripeCustomerId is null ? order.CustomerEmail : null,
            LineItems = [lineItem],
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            ClientReferenceId = order.Id.ToString("D"),
            Metadata = metadata,
            SubscriptionData = new SessionSubscriptionDataOptions { Metadata = metadata },
            // Leave other methods dynamic; never accidentally offer unsupported Pix recurrence.
            ExcludedPaymentMethodTypes = allowPix ? null : ["pix"],
            PaymentMethodOptions = allowPix ? new SessionPaymentMethodOptionsOptions
            {
                Pix = new SessionPaymentMethodOptionsPixOptions
                {
                    AmountIncludesIof = pix.AmountIncludesIof,
                    MandateOptions = new SessionPaymentMethodOptionsPixMandateOptionsOptions
                    {
                        Amount = order.FinalAmountMinorUnits(),
                        AmountType = "maximum",
                        AmountIncludesIof = pix.AmountIncludesIof,
                        Currency = "brl",
                        PaymentSchedule = order.BillingPeriod switch
                        {
                            BillingPeriod.Monthly => "monthly",
                            BillingPeriod.Quarterly => "quarterly",
                            BillingPeriod.Annual => "yearly",
                            _ => throw new InvalidOperationException("Unsupported Pix billing period.")
                        }
                    }
                }
            } : null,
            Locale = ToStripeLocale(request.Locale)
        };
    }

    private async Task RedeemPromotionForPaidOrderAsync(
        Order order,
        Session session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var metadata = session.Metadata;
        if (metadata is null
            || !metadata.TryGetValue(MetadataKeys.PromotionCodeId, out var rawPromotionId)
            || !metadata.TryGetValue(MetadataKeys.PromotionDiscountCents, out var rawDiscount)
            || !Guid.TryParse(rawPromotionId, out var promotionId)
            || !long.TryParse(rawDiscount, NumberStyles.None, CultureInfo.InvariantCulture, out var discountCents)
            || discountCents <= 0)
        {
            return;
        }

        await _promotions.RedeemAsync(
            promotionId,
            order.Id,
            discountCents,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    private static long ToMinorUnits(decimal amount) =>
        checked((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero));

    private static bool TryNormaliseReturnOrigin(string value, out string origin)
    {
        origin = string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static string DescribePlan(PricedPlan priced)
    {
        var plan = priced.Plan;

        return $"{plan.MemoryGb} GB RAM, {plan.CpuPercent}% CPU allocation, {plan.DiskGb} GB NVMe. "
            + $"Billed every {priced.Quote.Months} month(s).";
    }

    /// <summary>Stripe accepts a short list of locales; anything else falls back to auto-detect.</summary>
    private static string ToStripeLocale(string? locale) =>
        locale?.Trim().ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "pt-BR",
            "en" or "en-us" or "en-gb" => "en",
            _ => "auto"
        };
}

/// <summary>Money helpers for orders.</summary>
public static class OrderMoney
{
    /// <summary>The order's final amount in the currency's minor unit, as Stripe reports it.</summary>
    public static long FinalAmountMinorUnits(this Order order) =>
        (long)Math.Round(order.FinalAmount * 100m, MidpointRounding.AwayFromZero);
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
