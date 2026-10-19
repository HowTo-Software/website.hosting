using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using HowToSoftware.Hosting.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Infrastructure.Security;

/// <summary>Names for rate-limit policies attached to public endpoints.</summary>
public static class SecurityRateLimitPolicies
{
    public const string PaymentStatus = "payment-status";
    public const string StripeWebhook = "stripe-webhook";
    public const string Health = "health";
    public const string PublicPages = "public-pages";
    public const string TrialsRequest = "trial-request";
    public const string TrialsConfirm = "trial-confirm";
    public const string CustomerRequest = "customer-request";
    public const string CustomerAction = "customer-action";
}

/// <summary>Registers limits that belong to the application rather than to a CDN or WAF.</summary>
public static class SecurityServiceExtensions
{
    /// <summary>Adds antiforgery, request throttling and security-related framework settings.</summary>
    public static IServiceCollection AddSiteSecurity(
        this IServiceCollection services,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;

            foreach (var raw in configuration.GetSection("Security:KnownProxies").Get<string[]>() ?? [])
            {
                if (IPAddress.TryParse(raw, out var address))
                {
                    options.KnownProxies.Add(address);
                }
            }
        });
        services.AddAntiforgery(ConfigureAntiforgery);
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            // Enable only after every current and future subdomain is confirmed HTTPS-only.
            options.IncludeSubDomains = false;
            options.Preload = false;
        });
        services.AddHttpsRedirection(options =>
            options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers["Retry-After"] = "60";
                return ValueTask.CompletedTask;
            };

            options.AddPolicy(SecurityRateLimitPolicies.PaymentStatus, context =>
                FixedWindow(context, permitLimit: 60));
            options.AddPolicy(SecurityRateLimitPolicies.StripeWebhook, context =>
                FixedWindow(context, permitLimit: 120));
            options.AddPolicy(SecurityRateLimitPolicies.Health, context =>
                FixedWindow(context, permitLimit: 20));
            options.AddPolicy(SecurityRateLimitPolicies.TrialsRequest, context =>
                FixedWindow(context, permitLimit: 3, partition: "trial-request"));
            options.AddPolicy(SecurityRateLimitPolicies.TrialsConfirm, context =>
                FixedWindow(context, permitLimit: 10, partition: "trial-confirm"));
            options.AddPolicy(SecurityRateLimitPolicies.CustomerRequest, context => FixedWindow(context, 3, "customer-request"));
            options.AddPolicy(SecurityRateLimitPolicies.CustomerAction, context => FixedWindow(context, 12, "customer-action"));
            options.AddPolicy(SecurityRateLimitPolicies.PublicPages, context =>
            {
                var isCheckoutPost = HttpMethods.IsPost(context.Request.Method)
                    && context.Request.Path.StartsWithSegments("/game-hosting");

                return FixedWindow(
                    context,
                    permitLimit: isCheckoutPost ? 10 : 300,
                    partition: isCheckoutPost ? "checkout" : "page");
            });
        });

        return services;
    }

    private static void ConfigureAntiforgery(AntiforgeryOptions options)
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Strict;
        // SameAsRequest is transport-aware: HTTPS (including a trusted forwarded HTTPS request)
        // still receives a Secure cookie, while direct HTTP development/LAN rendering does not
        // throw before the page can load. Production HTTP is redirected before tokens are issued
        // whenever an HTTPS endpoint is configured.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // A __Host- prefix requires Secure even on HTTP. Browsers discard that combination
        // on direct LAN deployments, making every form POST fail CSRF validation. Keep a
        // host-only cookie (no Domain) with a name valid on both supported transports.
        options.Cookie.Name = "HTS.Antiforgery.v3";
        options.HeaderName = "X-HTS-CSRF";
    }

    private static RateLimitPartition<string> FixedWindow(
        HttpContext context,
        int permitLimit,
        string partition = "endpoint")
    {
        // RemoteIpAddress is populated by the server. Do not read X-Forwarded-For directly: it
        // is attacker-controlled unless a known reverse proxy has been configured explicitly.
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"{partition}:{address}";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1)
        });
    }
}

/// <summary>
/// Adds browser security boundaries to every response and creates the nonce used by the few
/// inline scripts required before Blazor starts.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private const string NonceKey = "HowToSoftware.Hosting.CspNonce";
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _environment;
    private readonly SiteOptions _site;

    public SecurityHeadersMiddleware(
        RequestDelegate next,
        IWebHostEnvironment environment,
        IOptions<SiteOptions> site)
    {
        _next = next;
        _environment = environment;
        _site = site.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        context.Items[NonceKey] = nonce;
        context.Response.OnStarting(() =>
        {
            ApplyResponseHeaders(context, nonce);
            return Task.CompletedTask;
        });

        await _next(context);
    }

    private void ApplyResponseHeaders(HttpContext context, string nonce)
    {
        var headers = context.Response.Headers;
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), browsing-topics=()";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";
        headers["X-XSS-Protection"] = "0";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        // CSP is a document policy. Omitting its per-response nonce from images, fonts and CSS
        // avoids adding hundreds of header bytes to every immutable static asset.
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) is true)
        {
            headers["Content-Security-Policy"] = BuildContentSecurityPolicy(context, nonce);
            // Both browser region and an explicit language cookie affect the rendered document.
            // Never let a shared cache serve another visitor's language/currency variant.
            var vary = headers.Vary.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            headers.Vary = string.Join(", ", vary.Concat(["Accept-Language", "Cookie"]).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        if (IsPaymentPath(context.Request.Path))
        {
            headers.CacheControl = "no-store, max-age=0";
            headers.Pragma = "no-cache";
        }

    }

    /// <summary>Gets the per-response nonce without ever rendering it as application data.</summary>
    public static string? GetNonce(HttpContext? context) =>
        context?.Items.TryGetValue(NonceKey, out var value) is true ? value as string : null;

    private string BuildContentSecurityPolicy(HttpContext context, string nonce)
    {
        var connectSource = "'self'";

        // Firefox does not consistently treat an HTTP(S) 'self' source as authorising the
        // equivalent WebSocket scheme. Add only this request's exact authority so Blazor Server
        // can connect on localhost, a LAN IP, or the public host without a wildcard policy.
        if (context.Request.Host.HasValue)
        {
            var webSocketScheme = context.Request.IsHttps ? "wss" : "ws";
            connectSource += $" {webSocketScheme}://{context.Request.Host.ToUriComponent()}";
        }

        if (!_environment.IsDevelopment()
            && Uri.TryCreate(_site.BaseUrl, UriKind.Absolute, out var siteUri))
        {
            var publicWebSocketOrigin = $"wss://{siteUri.Authority}";
            if (!connectSource.Contains(publicWebSocketOrigin, StringComparison.OrdinalIgnoreCase))
            {
                connectSource += $" {publicWebSocketOrigin}";
            }
        }

        // The directive rewrites every HTTP subresource URL to HTTPS in the browser. It is safe
        // only when the document itself arrived over HTTPS. Request.IsHttps already reflects a
        // trusted X-Forwarded-Proto because UseForwardedHeaders runs before this middleware.
        var upgrade = context.Request.IsHttps ? " upgrade-insecure-requests;" : string.Empty;

        return "default-src 'self'; "
            + "base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; "
            + $"script-src 'self' 'nonce-{nonce}'; "
            // Razor components use a small number of CSS custom properties in style attributes.
            // Stylesheets remain same-origin; unsafe-eval and remote CSS are never allowed.
            + "style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
            + $"connect-src {connectSource}; manifest-src 'self'; media-src 'self'; worker-src 'self';"
            + upgrade;
    }

    private static bool IsPaymentPath(PathString path) =>
        path.StartsWithSegments("/payment") || path.StartsWithSegments("/api/payments")
        || path.StartsWithSegments("/account");
}

/// <summary>Pipeline registration kept next to the middleware so it cannot be half-installed.</summary>
public static class SecurityApplicationExtensions
{
    public static IApplicationBuilder UseSiteSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
