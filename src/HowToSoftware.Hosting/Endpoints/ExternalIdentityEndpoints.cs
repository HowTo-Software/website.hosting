using HowToSoftware.Hosting.Infrastructure.Security;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Services.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Endpoints;

public static class ExternalFlowCookie
{
    public const string Name = "HTS.ExternalFlow.v1";
    public static string? Read(HttpContext context) => context.Request.Cookies[Name];
    private static CookieOptions Options(HttpContext context) => new()
    { Path = "/account", HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
      IsEssential = true, MaxAge = TimeSpan.FromMinutes(10) };
    public static void Issue(HttpContext context, string secret) => context.Response.Cookies.Append(Name, secret, Options(context));
    public static void Clear(HttpContext context) => context.Response.Cookies.Delete(Name, Options(context));
}

public static class ExternalIdentityEndpoints
{
    public static IEndpointRouteBuilder MapExternalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/account/connections/start", LinkAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        endpoints.MapPost("/account/access/external", LoginAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerRequest);
        endpoints.MapPost("/account/connections/confirm", CompleteAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        endpoints.MapPost("/account/connections/unlink", UnlinkAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        // Disabled callbacks return 404; enabled protocol handlers intercept them after global rate limiting.
        endpoints.MapMethods(ExternalProviders.DiscordCallback, ["GET", "POST"], () => Results.NotFound())
            .RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        endpoints.MapMethods(ExternalProviders.DirectoryCallback, ["GET", "POST"], () => Results.NotFound())
            .RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        return endpoints;
    }
    private static Task<IResult> LinkAsync([FromForm] ExternalProviderForm form, HttpContext context,
        IExternalIdentityService identities, IOptions<ExternalIdentityOptions> settings, IOptions<SiteOptions> site) =>
        BeginAsync(form.Provider ?? "", "link", context, identities, settings.Value, site.Value);
    private static Task<IResult> LoginAsync([FromForm] ExternalProviderForm form, HttpContext context,
        IExternalIdentityService identities, IOptions<ExternalIdentityOptions> settings, IOptions<SiteOptions> site) =>
        BeginAsync(form.Provider ?? "", "login", context, identities, settings.Value, site.Value);
    private static Task<IResult> BeginAsync(string provider, string purpose, HttpContext context,
        IExternalIdentityService identities, ExternalIdentityOptions settings, SiteOptions site) => GuardAsync(context, async () =>
        {
            if (!(purpose == "link" ? settings.LinkingEnabled(provider) : settings.LoginEnabled(provider))) return Results.NotFound();
            var canonical = new Uri(site.BaseUrl);
            if (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, canonical.Authority, StringComparison.OrdinalIgnoreCase))
                return Results.LocalRedirect("/account?notice=connection-failed");
            var flow = await identities.BeginAsync(provider, purpose, CustomerSessionCookie.Read(context), context.RequestAborted);
            if (flow is null) return Results.LocalRedirect("/account?notice=connection-failed");
            ExternalFlowCookie.Issue(context, flow.BrowserSecret);
            var properties = new AuthenticationProperties
            {
                RedirectUri = "/account/connections/return",
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
            };
            properties.Items[ExternalIdentityRegistration.FlowItem] = flow.Id.ToString("N");
            properties.Items[ExternalIdentityRegistration.ProviderItem] = provider;
            return Results.Challenge(properties, [ExternalProviders.Scheme(provider)]);
        });
    private static Task<IResult> CompleteAsync([FromForm] ExternalConfirmationForm form, HttpContext context,
        IExternalIdentityService identities) => GuardAsync(context, async () =>
        {
            if (!context.Request.IsHttps) return Results.LocalRedirect("/account?notice=connection-failed");
            var result = await identities.CompleteAsync(form.FlowId, ExternalFlowCookie.Read(context),
                CustomerSessionCookie.Read(context), context.RequestAborted);
            ExternalFlowCookie.Clear(context);
            if (!result.Success) return Results.LocalRedirect("/account?notice=connection-failed");
            if (result.SessionToken is { } session)
                context.Response.Cookies.Append(CustomerSessionCookie.Name, session, CustomerSessionCookie.Options(context));
            return Results.LocalRedirect("/account?tab=account&notice=connection-complete");
        });
    private static Task<IResult> UnlinkAsync([FromForm] ExternalProviderForm form, HttpContext context,
        IExternalIdentityService identities) => GuardAsync(context, async () =>
        {
            var ok = await identities.UnlinkAsync(form.Provider ?? "", CustomerSessionCookie.Read(context), context.RequestAborted);
            return Results.LocalRedirect(ok ? "/account?tab=account&notice=connection-removed" : "/account?notice=connection-failed");
        });
    private static async Task<IResult> GuardAsync(HttpContext context, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            ExternalIdentityRegistration.LogFailure(context, e);
            return Results.LocalRedirect("/account?notice=connection-failed");
        }
    }
}
public sealed class ExternalProviderForm { public string? Provider { get; set; } }
public sealed class ExternalConfirmationForm { public Guid FlowId { get; set; } }
