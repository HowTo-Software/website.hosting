using global::Stripe;
using global::Stripe.Checkout;
using HowToSoftware.Hosting.Services.Orders;
using HowToSoftware.Hosting.Services.Subscriptions;

namespace HowToSoftware.Hosting.Services.Payments;

/// <summary>
/// Routes a verified Stripe event to the right handler, exactly once.
/// </summary>
public interface IStripeWebhookHandler
{
    /// <summary>
    /// Handles an event whose signature has already been checked.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the event was acted on; <see langword="false"/> when it was
    /// a redelivery of something already handled, or a type the site does not care about. Both
    /// are acknowledged to Stripe with a 200, so it stops retrying.
    /// </returns>
    Task<bool> HandleAsync(Event stripeEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IStripeWebhookHandler"/> for the events the purchase flow depends on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fulfilment is driven from here and nowhere else.</b> The success page the customer lands
/// on after paying is presentation; a browser reaching it proves nothing. A server is created
/// because a signed <c>checkout.session.completed</c> arrived with <c>payment_status=paid</c>
/// and an amount that matched what the server computed.
/// </para>
/// <para>
/// Stripe delivers at least once. The event id is recorded before anything is acted on, and a
/// second delivery of the same id is acknowledged and dropped.
/// </para>
/// </remarks>
public sealed class StripeWebhookHandler : IStripeWebhookHandler
{
    private readonly IStripeCheckoutService _checkout;
    private readonly IOrderStore _orders;
    private readonly ILogger<StripeWebhookHandler> _logger;
    private readonly ISubscriptionManagement? _subscriptions;

    /// <summary>Creates the handler.</summary>
    public StripeWebhookHandler(
        IStripeCheckoutService checkout,
        IOrderStore orders,
        ILogger<StripeWebhookHandler> logger,
        ISubscriptionManagement? subscriptions = null)
    {
        _checkout = checkout;
        _orders = orders;
        _logger = logger;
        _subscriptions = subscriptions;
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(Event stripeEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stripeEvent);

        if (!IsRelevant(stripeEvent.Type))
        {
            _logger.LogDebug("Stripe event {Type} is not handled by this site; acknowledged.", stripeEvent.Type);
            return false;
        }

        if (!await _orders.TryRecordEventAsync(stripeEvent.Id, stripeEvent.Type, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Stripe event {EventId} ({Type}) was already handled; acknowledged.", stripeEvent.Id, stripeEvent.Type);
            return false;
        }

        try
        {
            switch (stripeEvent.Type)
            {
                case EventTypes.CheckoutSessionCompleted:
                case EventTypes.CheckoutSessionAsyncPaymentSucceeded:
                    if (stripeEvent.Data.Object is Session completed)
                    {
                        await _checkout.HandleCompletedCheckoutAsync(completed, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case EventTypes.CheckoutSessionAsyncPaymentFailed:
                case EventTypes.CheckoutSessionExpired:
                    if (stripeEvent.Data.Object is Session ended)
                    {
                        await _checkout.HandleExpiredCheckoutAsync(ended, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case EventTypes.CustomerSubscriptionCreated:
                case EventTypes.CustomerSubscriptionUpdated:
                case EventTypes.CustomerSubscriptionDeleted:
                case EventTypes.CustomerSubscriptionPaused:
                case EventTypes.CustomerSubscriptionResumed:
                    if (stripeEvent.Data.Object is Subscription subscription)
                    {
                        await _checkout.HandleSubscriptionStateAsync(subscription, cancellationToken).ConfigureAwait(false);
                        if (_subscriptions is not null) await _subscriptions.ReconcileAsync(subscription.Id, cancellationToken);
                    }

                    break;

                case EventTypes.InvoicePaid:
                case EventTypes.InvoicePaymentFailed:
                    if (stripeEvent.Data.Object is Invoice invoice)
                    {
                        await _checkout.HandleInvoiceStateAsync(invoice, cancellationToken).ConfigureAwait(false);
                        if (_subscriptions is not null) await _subscriptions.ReconcileAsync(invoice.Parent?.SubscriptionDetails?.SubscriptionId, cancellationToken);
                    }

                    break;
            }
        }
        catch
        {
            // A failed handler returns 5xx and Stripe retries. Release the idempotency claim so
            // that retry performs the work instead of being mistaken for a completed delivery.
            await _orders.ReleaseEventAsync(stripeEvent.Id, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return true;
    }

    /// <summary>The event types this site subscribes to. Anything else is acknowledged and ignored.</summary>
    public static bool IsRelevant(string? type) => type is
        EventTypes.CheckoutSessionCompleted
        or EventTypes.CheckoutSessionAsyncPaymentSucceeded
        or EventTypes.CheckoutSessionAsyncPaymentFailed
        or EventTypes.CheckoutSessionExpired
        or EventTypes.CustomerSubscriptionCreated
        or EventTypes.CustomerSubscriptionUpdated
        or EventTypes.CustomerSubscriptionDeleted
        or EventTypes.CustomerSubscriptionPaused
        or EventTypes.CustomerSubscriptionResumed
        or EventTypes.InvoicePaid
        or EventTypes.InvoicePaymentFailed;
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
