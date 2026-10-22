using System.Net;

namespace HowToSoftware.Hosting.Models;

/// <summary>
/// Every route the purchase flow links to, in one place.
/// </summary>
/// <remarks>
/// A route string typed into a component is a route that will be typed slightly differently in
/// the next component. These are referenced from the pages, the navigation, the Stripe return
/// URLs and the tests, so a rename happens once.
/// </remarks>
public static class SiteRoutes
{
    /// <summary>The game catalogue: what can be hosted.</summary>
    public const string GameHosting = "/game-hosting";

    /// <summary>The Project Zomboid hosting page.</summary>
    public const string ProjectZomboid = "/game-hosting/project-zomboid";

    /// <summary>The plan picker on the Project Zomboid page.</summary>
    public const string ProjectZomboidPlans = "/game-hosting/project-zomboid#plans";

    /// <summary>
    /// The old Project Zomboid route. Answered with a permanent redirect so links already out in
    /// the world keep working.
    /// </summary>
    public const string LegacyProjectZomboid = "/project-zomboid";

    /// <summary>Where Stripe sends the customer after a successful payment.</summary>
    public const string PaymentSuccess = "/payment/success";

    /// <summary>Where Stripe sends the customer if they back out of Checkout.</summary>
    public const string PaymentCancel = "/payment/cancel";

    /// <summary>The Stripe webhook receiver.</summary>
    public const string StripeWebhook = "/api/payments/stripe/webhook";

    /// <summary>Order status for the success page to poll.</summary>
    public const string OrderStatus = "/api/payments/status";

    /// <summary>
    /// The control panel's sign-in page. A different application on a different host, so it
    /// is absolute and must never be written as a site-relative route.
    /// </summary>
    public const string PanelLogin = "https://panel.howto.software/auth/login";

    /// <summary>A game's own page.</summary>
    public static string Game(string gameSlug) => $"{GameHosting}/{gameSlug}";

    /// <summary>The review step for a plan on a game, with the chosen period.</summary>
    public static string Review(string gameSlug, string planSlug, BillingPeriod period, string? currency = null) =>
        $"{GameHosting}/{gameSlug}/review?plan={WebUtility.UrlEncode(planSlug)}&period={BillingPolicy.Slug(period)}"
        + (currency is null ? string.Empty : $"&currency={WebUtility.UrlEncode(currency)}");

    /// <summary>The cancel page for a specific order.</summary>
    public static string PaymentCancelFor(Guid orderId) => $"{PaymentCancel}?order={orderId:D}";

    /// <summary>The success page for a Stripe Checkout session.</summary>
    public static string PaymentSuccessFor(string sessionId) =>
        $"{PaymentSuccess}?session_id={WebUtility.UrlEncode(sessionId)}";
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
