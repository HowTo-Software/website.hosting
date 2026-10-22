using HowToSoftware.Hosting.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Infrastructure.Stripe;

/// <summary>
/// Stripe settings, bound from the <c>Stripe</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>The secret key and the webhook secret are never in appsettings.json.</b> They arrive from
/// the environment (<c>Stripe__SecretKey</c>, <c>Stripe__WebhookSecret</c>) or from
/// <c>dotnet user-secrets</c> in development, and they stay on the server: nothing here is
/// handed to a component that renders, nothing is logged, and <see cref="ToString"/> is
/// overridden so an options object cannot be printed by accident.
/// </para>
/// <para>
/// The site serves every marketing page without any of these. Only the "continue to payment"
/// button needs them, and it says so on the page when they are absent.
/// </para>
/// </remarks>
public sealed class StripeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Stripe";

    /// <summary>Pix Automático requires explicit activation after account-capability and staging validation.</summary>
    public StripePixSubscriptionsOptions PixSubscriptions { get; set; } = new();

    /// <summary>
    /// The publishable key (<c>pk_…</c>). Safe for a browser, but the site does not currently
    /// use Stripe.js, so it is held only for the day it does.
    /// </summary>
    public string PublishableKey { get; set; } = string.Empty;

    /// <summary>The secret key (<c>sk_…</c>). Server only.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>The webhook signing secret (<c>whsec_…</c>). Server only.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Absolute URL Stripe returns the customer to after payment. May contain
    /// <c>{CHECKOUT_SESSION_ID}</c>, which Stripe substitutes. When empty the site builds it from
    /// the origin of the request that started Checkout.
    /// </summary>
    public string SuccessUrl { get; set; } = string.Empty;

    /// <summary>
    /// Absolute URL Stripe returns the customer to if they back out. It may contain the public
    /// placeholders <c>{GAME_SLUG}</c>, <c>{PLAN_SLUG}</c> and <c>{BILLING_PERIOD}</c>. Internal
    /// order identifiers are deliberately never put in this URL.
    /// </summary>
    public string CancelUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional pre-created Stripe Price ids, keyed <c>{plan-slug}:{period-slug}</c>, e.g.
    /// <c>zomboid-8gb:quarterly</c>. When a key is present its Price is used as the line item;
    /// otherwise the site sends an inline recurring price it computed itself.
    /// </summary>
    public Dictionary<string, string> PriceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Seconds of clock drift tolerated when checking a webhook signature. Stripe's default.
    /// </summary>
    public int WebhookToleranceSeconds { get; set; } = 300;

    /// <summary>Whether Checkout sessions can be created.</summary>
    public bool IsConfigured =>
        !EnvironmentFile.IsPlaceholder(SecretKey)
        && (SecretKey.StartsWith("sk_", StringComparison.Ordinal)
            || SecretKey.StartsWith("rk_", StringComparison.Ordinal));

    /// <summary>Whether webhook deliveries can be verified.</summary>
    public bool IsWebhookConfigured =>
        !EnvironmentFile.IsPlaceholder(WebhookSecret)
        && WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal);

    /// <summary>Looks up a configured Price id for a plan and period.</summary>
    public string? FindPriceId(string planSlug, string periodSlug, string currency = "usd")
    {
        if (!Services.StorefrontCurrencyService.TryNormalize(currency, out var selected)) return null;
        if (PriceIds.TryGetValue($"{planSlug}:{periodSlug}:{selected}", out var id) && !string.IsNullOrWhiteSpace(id))
            return id.Trim();
        // Old unqualified keys always mean USD, never a price reusable across currencies.
        return selected == "usd" && PriceIds.TryGetValue($"{planSlug}:{periodSlug}", out id) && !string.IsNullOrWhiteSpace(id)
            ? id.Trim() : null;
    }

    /// <summary>
    /// Describes the secret key without revealing it: its prefix and length only.
    /// </summary>
    public string DescribeSecretKey() => Describe(SecretKey);

    /// <summary>Describes the webhook secret without revealing it.</summary>
    public string DescribeWebhookSecret() => Describe(WebhookSecret);

    private static string Describe(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "not set";
        }

        var underscore = secret.IndexOf('_', StringComparison.Ordinal);
        var prefix = underscore > 0 && underscore < 8 ? secret[..(underscore + 1)] : "?";

        return $"{prefix}… ({secret.Length} chars)";
    }

    /// <summary>Never the secrets. Ever.</summary>
    public override string ToString() => nameof(StripeOptions);
}

/// <summary>
/// Sanity checks on the Stripe settings, run on first use rather than at startup so a host with
/// no payment credentials still serves its pages.
/// </summary>
/// <remarks>
/// Every message describes the shape of the problem and never quotes a value. A validation
/// error ends up in a log.
/// </remarks>
public sealed class StripeOptionsValidator : IValidateOptions<StripeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, StripeOptions options)
    {
        var failures = new List<string>();

        if (options.PixSubscriptions.Enabled)
        {
            if (!StripePixSubscriptionsOptions.IsEligibleAccountCountry(options.PixSubscriptions.AccountCountry))
                failures.Add("Stripe:PixSubscriptions:AccountCountry must identify an eligible non-Brazilian Stripe account; Brazilian accounts cannot use Pix subscriptions.");
            if (options.PixSubscriptions.AmountIncludesIof is not ("always" or "never"))
                failures.Add("Stripe:PixSubscriptions:AmountIncludesIof must explicitly be always (merchant absorbs IOF) or never (customer pays IOF).");
        }

        if (!string.IsNullOrWhiteSpace(options.SecretKey)
            && !options.SecretKey.StartsWith("sk_", StringComparison.Ordinal)
            && !options.SecretKey.StartsWith("rk_", StringComparison.Ordinal))
        {
            failures.Add("Stripe:SecretKey does not look like a Stripe secret key (expected an sk_ or rk_ prefix).");
        }

        if (!string.IsNullOrWhiteSpace(options.PublishableKey)
            && !options.PublishableKey.StartsWith("pk_", StringComparison.Ordinal))
        {
            failures.Add("Stripe:PublishableKey does not look like a publishable key (expected a pk_ prefix).");
        }

        if (!string.IsNullOrWhiteSpace(options.WebhookSecret)
            && !options.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal))
        {
            failures.Add("Stripe:WebhookSecret does not look like a webhook signing secret (expected a whsec_ prefix).");
        }

        if (!string.IsNullOrWhiteSpace(options.SuccessUrl)
            && !IsSecurePublicUrl(options.SuccessUrl))
        {
            failures.Add("Stripe:SuccessUrl must use HTTPS (HTTP is accepted only for a loopback development URL).");
        }

        if (!string.IsNullOrWhiteSpace(options.CancelUrl)
            && !IsSecurePublicUrl(options.CancelUrl))
        {
            failures.Add("Stripe:CancelUrl must use HTTPS (HTTP is accepted only for a loopback development URL).");
        }

        if (options.WebhookToleranceSeconds is < 0 or > 3600)
        {
            failures.Add("Stripe:WebhookToleranceSeconds must be between 0 and 3600.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsSecurePublicUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}

/// <summary>Operational opt-in, not proof that Stripe has enabled the account capability.</summary>
public sealed class StripePixSubscriptionsOptions
{
    public bool Enabled { get; set; }
    public string AccountCountry { get; set; } = string.Empty;
    public string AmountIncludesIof { get; set; } = string.Empty;

    // Stripe's published eligible business countries, excluding BR (one-time Pix only).
    private static readonly HashSet<string> EligibleCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "AU", "BE", "BG", "CA", "CH", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR",
        "GB", "GR", "HR", "HU", "IE", "IT", "LI", "LT", "LU", "LV", "MT", "NL", "NO", "PL",
        "PT", "RO", "SE", "SG", "SI", "SK", "US"
    };
    public static bool IsEligibleAccountCountry(string? country) => country is not null && EligibleCountries.Contains(country);
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
