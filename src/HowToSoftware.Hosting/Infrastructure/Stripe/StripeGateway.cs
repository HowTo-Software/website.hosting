using global::Stripe;
using global::Stripe.Checkout;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Infrastructure.Stripe;

/// <summary>
/// The thin seam between this application and the Stripe SDK.
/// </summary>
/// <remarks>
/// <para>
/// Everything the site asks Stripe for goes through these four calls, so the payment logic in
/// <c>Services/Payments</c> can be tested against a fake without a network, and so the secret
/// key is read in exactly one class.
/// </para>
/// <para>
/// Deliberately not a general Stripe abstraction. It exposes the SDK's own option and result
/// types rather than re-modelling them; the value is in the seam, not in a second vocabulary.
/// </para>
/// </remarks>
public interface IStripeGateway
{
    /// <summary>Whether a secret key is configured, and therefore whether calls can be made.</summary>
    bool IsConfigured { get; }

    /// <summary>Creates a Checkout Session.</summary>
    Task<Session> CreateCheckoutSessionAsync(SessionCreateOptions options, CancellationToken cancellationToken = default);

    /// <summary>Reads a Checkout Session back by id.</summary>
    Task<Session> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a webhook delivery's signature and parses the event.
    /// </summary>
    /// <param name="json">The raw request body, byte for byte as received.</param>
    /// <param name="signatureHeader">The <c>Stripe-Signature</c> header.</param>
    /// <exception cref="StripeException">The signature is missing, malformed, stale or wrong.</exception>
    Event ConstructEvent(string json, string signatureHeader);
}

/// <summary>
/// <see cref="IStripeGateway"/> over the official Stripe .NET SDK.
/// </summary>
/// <remarks>
/// The client is built per call from the current options rather than cached, so a rotated key
/// takes effect without a restart. Constructing a <see cref="StripeClient"/> is cheap; it holds
/// no connection of its own.
/// </remarks>
public sealed partial class StripeGateway : IStripeGateway, ISubscriptionGateway
{
    private readonly IOptionsMonitor<StripeOptions> _options;

    /// <summary>Creates the gateway.</summary>
    public StripeGateway(IOptionsMonitor<StripeOptions> options) => _options = options;

    /// <inheritdoc />
    public bool IsConfigured => _options.CurrentValue.IsConfigured;

    /// <inheritdoc />
    public Task<Session> CreateCheckoutSessionAsync(SessionCreateOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Client().V1.Checkout.Sessions.CreateAsync(options, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public Task<Session> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return Client().V1.Checkout.Sessions.GetAsync(sessionId, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public Event ConstructEvent(string json, string signatureHeader)
    {
        var options = _options.CurrentValue;

        if (!options.IsWebhookConfigured)
        {
            throw new StripeException("The webhook signing secret is not configured.");
        }

        // throwOnApiVersionMismatch is off: a dashboard pinned to a newer API version than the
        // SDK would otherwise reject every delivery, and the fields this site reads have been
        // stable across versions. The signature check is unaffected by the flag.
        return EventUtility.ConstructEvent(
            json,
            signatureHeader,
            options.WebhookSecret,
            tolerance: options.WebhookToleranceSeconds,
            throwOnApiVersionMismatch: false);
    }

    private StripeClient Client()
    {
        var options = _options.CurrentValue;

        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Stripe is not configured. Set Stripe__SecretKey in the environment or with dotnet user-secrets.");
        }

        return new StripeClient(options.SecretKey);
    }
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
