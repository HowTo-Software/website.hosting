using System.Globalization;
using HowToSoftware.Hosting.Infrastructure.Security;
using HowToSoftware.Hosting.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;

namespace HowToSoftware.Hosting.Endpoints;

public static class CustomerSessionCookie
{
    public const string Name = "HTS.CustomerSession.v1";
    public static CookieOptions Options(HttpContext context) => new()
    {
        HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
        IsEssential = true, Path = "/account", MaxAge = TimeSpan.FromMinutes(30)
    };
    public static string? Read(HttpContext context) => context.Request.Cookies[Name];
}

public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/account/access/request", RequestAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerRequest);
        endpoints.MapPost("/account/access/confirm", ConfirmAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        endpoints.MapPost("/account/logout", LogoutAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        endpoints.MapPost("/account/support", SupportAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerRequest);
        endpoints.MapPost("/account/plans/preview", PreviewAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        endpoints.MapPost("/account/plans/confirm", ChangeAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        endpoints.MapPost("/account/plans/cancel", CancelAsync).RequireRateLimiting(SecurityRateLimitPolicies.CustomerAction);
        return endpoints;
    }
    private static async Task<IResult> GuardAsync(HttpContext context, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (InvalidOperationException e)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CustomerEndpoints")
                .LogWarning("Customer action could not complete ({FailureClass}).", e.GetType().Name);
            return Results.LocalRedirect("/account?notice=failed");
        }
    }
    private static Task<IResult> RequestAsync([FromForm] CustomerAccessForm input, HttpContext context, ICustomerAccess access) =>
        GuardAsync(context, async () => Results.LocalRedirect(
            await access.RequestAsync(input.Email ?? "", context.RequestAborted) ? "/account?notice=sent" : "/account?notice=unavailable"));
    private static Task<IResult> ConfirmAsync([FromForm] CustomerConfirmationForm input, HttpContext context, ICustomerAccess access) =>
        GuardAsync(context, async () =>
        {
            var session = await access.ConfirmAsync(input.Token ?? "", context.RequestAborted);
            if (session is null) return Results.LocalRedirect("/account?notice=invalid");
            context.Response.Cookies.Append(CustomerSessionCookie.Name, session, CustomerSessionCookie.Options(context));
            return Results.LocalRedirect("/account");
        });
    private static Task<IResult> LogoutAsync([FromForm] CustomerActionForm input, HttpContext context, ICustomerAccess access) =>
        GuardAsync(context, async () =>
        {
            await access.RevokeAsync(CustomerSessionCookie.Read(context), context.RequestAborted);
            context.Response.Cookies.Delete(CustomerSessionCookie.Name, CustomerSessionCookie.Options(context));
            return Results.LocalRedirect("/account");
        });
    private static Task<IResult> SupportAsync([FromForm] CustomerSupportForm input, HttpContext context, ICustomerAccess access, ICustomerWorkspace workspace) =>
        GuardAsync(context, async () =>
        {
            var owner = await access.OwnerAsync(CustomerSessionCookie.Read(context), context.RequestAborted);
            if (owner is null) return Results.LocalRedirect("/account?notice=invalid");
            var ok = await workspace.SupportAsync(owner, input.Subject ?? "", input.Message ?? "", CultureInfo.CurrentUICulture.Name, context.RequestAborted);
            return Results.LocalRedirect(ok ? "/account?tab=support&notice=ticket" : "/account?tab=support&notice=failed");
        });
    private static Task<IResult> PreviewAsync([FromForm] CustomerPlanForm input, HttpContext context, ICustomerAccess access) =>
        GuardAsync(context, async () =>
        {
            var owner = await access.OwnerAsync(CustomerSessionCookie.Read(context), context.RequestAborted);
            var management = context.RequestServices.GetService<ISubscriptionManagement>();
            if (owner is null || management?.Enabled != true) return Results.LocalRedirect("/account?notice=invalid");
            var result = await management.PreviewAsync(owner, input.ServiceId, input.PlanId, context.RequestAborted);
            return Results.LocalRedirect("/account?tab=plans&notice=" + (result.Success ? "quoted" : SafeCode(result.Code)));
        });
    private static Task<IResult> ChangeAsync([FromForm] CustomerActionForm input, HttpContext context, ICustomerAccess access) =>
        GuardAsync(context, async () =>
        {
            var owner = await access.OwnerAsync(CustomerSessionCookie.Read(context), context.RequestAborted);
            var management = context.RequestServices.GetService<ISubscriptionManagement>();
            if (owner is null || management?.Enabled != true) return Results.LocalRedirect("/account?notice=invalid");
            var result = await management.ConfirmAsync(owner, input.ChangeId, context.RequestAborted);
            return Results.LocalRedirect("/account?tab=plans&notice=" + (result.Success ? "success" : SafeCode(result.Code)));
        });
    private static Task<IResult> CancelAsync([FromForm] CustomerActionForm input, HttpContext context, ICustomerAccess access) =>
        GuardAsync(context, async () =>
        {
            var owner = await access.OwnerAsync(CustomerSessionCookie.Read(context), context.RequestAborted);
            var management = context.RequestServices.GetService<ISubscriptionManagement>();
            if (owner is null || management is null) return Results.LocalRedirect("/account?notice=invalid");
            var ok = await management.CancelScheduledAsync(owner, input.ChangeId, context.RequestAborted);
            return Results.LocalRedirect("/account?tab=plans&notice=" + (ok ? "success" : "failed"));
        });
    private static string SafeCode(string code) => code == "invalid" ? "selection" : code is "disabled" or "pending" or "interval" or "unpaid" or "capacity" or "expired" or "stale" or "renewing" or "payment-method" ? code : "failed";
}
public sealed class CustomerAccessForm { public string? Email { get; set; } }
public sealed class CustomerConfirmationForm { public string? Token { get; set; } }
public sealed class CustomerActionForm { public Guid ChangeId { get; set; } }
public sealed class CustomerPlanForm { public Guid ServiceId { get; set; } public Guid PlanId { get; set; } }
public sealed class CustomerSupportForm { public string? Subject { get; set; } public string? Message { get; set; } }
