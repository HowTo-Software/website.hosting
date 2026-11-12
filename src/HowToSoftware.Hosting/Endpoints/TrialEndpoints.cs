using System.Globalization;
using HowToSoftware.Hosting.Services.Identity;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.AspNetCore.Mvc;

namespace HowToSoftware.Hosting.Endpoints;

public static class TrialEndpointPolicies
{
    public const string Request = "trial-request";
    public const string Confirm = "trial-confirm";
}

/// <summary>Native form posts with automatic antiforgery and dedicated request limits.</summary>
public static class TrialEndpoints
{
    public static IEndpointRouteBuilder MapTrialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/trials/request", RequestAsync).RequireRateLimiting(TrialEndpointPolicies.Request);
        endpoints.MapPost("/trials/recover", RecoverAsync).RequireRateLimiting(TrialEndpointPolicies.Request);
        endpoints.MapPost("/trials/confirm", ConfirmAsync).RequireRateLimiting(TrialEndpointPolicies.Confirm);
        endpoints.MapPost("/trials/panel-password", PanelPasswordAsync).RequireRateLimiting(TrialEndpointPolicies.Confirm);
        return endpoints;
    }

    private static async Task<IResult> RequestAsync([FromForm] TrialRequestForm input, HttpContext context,
        ITrialService trials, ILoggerFactory loggerFactory)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 254
            || string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 80
            || input.GameSlug is not ("project-zomboid" or "minecraft")
            || input.ProfileId?.Length > 64 || input.Version?.Length > 64)
        {
            return Results.LocalRedirect("/trial?notice=invalid");
        }

        try
        {
            var request = new TrialSelectionRequest(input.Email, input.DisplayName, input.GameSlug,
                input.ProfileId, input.Version, CultureInfo.CurrentUICulture.Name);
            var result = await trials.RequestAsync(request, context.RequestAborted);
            return Results.LocalRedirect(result.Outcome switch
            {
                TrialRequestOutcome.Sent => "/trial?notice=sent",
                TrialRequestOutcome.InvalidSelection => "/trial?notice=invalid",
                _ => "/trial?notice=unavailable"
            });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never repeat a posted email, verification token, SMTP response or SQL message.
            loggerFactory.CreateLogger("TrialEndpoints").LogWarning(
                "Trial request could not complete ({FailureClass}).", exception.GetType().Name);
            return Results.LocalRedirect("/trial?notice=unavailable");
        }
    }

    private static async Task<IResult> ConfirmAsync([FromForm] TrialConfirmationForm input, HttpContext context,
        ITrialService trials, ILoggerFactory loggerFactory)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (input.VerificationToken is not { Length: > 0 and <= 4096 })
        {
            return Results.LocalRedirect("/trial?notice=invalid-token");
        }

        try
        {
            var result = await trials.ConfirmAsync(input.VerificationToken, context.RequestAborted);

            if (result.Outcome == TrialConfirmationOutcome.Confirmed && result.AccessToken is { Length: > 0 } accessToken)
            {
                TrialAccessCookie.Issue(context, accessToken);
                return Results.LocalRedirect("/trial");
            }

            return Results.LocalRedirect(result.Outcome switch
            {
                TrialConfirmationOutcome.AlreadyClaimed => "/trial?notice=claimed",
                TrialConfirmationOutcome.Unavailable => "/trial?notice=unavailable",
                _ => "/trial?notice=invalid-token"
            });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("TrialEndpoints").LogWarning(
                "Trial confirmation could not complete ({FailureClass}).", exception.GetType().Name);
            return Results.LocalRedirect("/trial?notice=unavailable");
        }
    }

    private static async Task<IResult> RecoverAsync([FromForm] TrialRecoveryForm input, HttpContext context,
        ITrialService trials, ILoggerFactory loggerFactory)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (input.Email is not { Length: > 0 and <= 254 }) return Results.LocalRedirect("/trial?notice=invalid");

        try
        {
            var result = await trials.RecoverAccessAsync(input.Email, CultureInfo.CurrentUICulture.Name, context.RequestAborted);
            return Results.LocalRedirect(result.Outcome == TrialRequestOutcome.Sent
                ? "/trial?notice=sent" : "/trial?notice=unavailable");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("TrialEndpoints").LogWarning(
                "Trial owner recovery could not complete ({FailureClass}).", exception.GetType().Name);
            return Results.LocalRedirect("/trial?notice=unavailable");
        }
    }

    /// <summary>
    /// Hands the verified trial owner to a one-time Authentik page to choose the panel's SSO password.
    /// The owner cookie is the proof of email ownership; the form carries nothing else.
    /// </summary>
    private static async Task<IResult> PanelPasswordAsync([FromForm] TrialPanelPasswordForm input, HttpContext context,
        ITrialService trials, IPanelPasswordSetup passwords, ILoggerFactory loggerFactory)
    {
        context.Response.Headers.CacheControl = "no-store";
        const string unavailable = "/trial?notice=panel-password-unavailable";
        if (!passwords.IsConfigured || TrialAccessCookie.Read(context) is not { } accessToken)
        {
            return Results.LocalRedirect(unavailable);
        }

        try
        {
            var account = await trials.GetByAccessTokenAsync(accessToken, context.RequestAborted);
            if (account is null || !TrialPanelAccess.CanSetPassword(account.State))
            {
                return Results.LocalRedirect(unavailable);
            }

            var result = await passwords.CreateLinkAsync(account.Email, account.DisplayName, context.RequestAborted);
            return result switch
            {
                { Outcome: PanelPasswordSetupOutcome.Ready, Link: { } link } => Results.Redirect(link.AbsoluteUri),
                { Outcome: PanelPasswordSetupOutcome.DirectoryAccount } => Results.LocalRedirect("/trial?notice=panel-directory"),
                _ => Results.LocalRedirect(unavailable)
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("TrialEndpoints").LogWarning(
                "Trial panel password setup could not complete ({FailureClass}).", exception.GetType().Name);
            return Results.LocalRedirect(unavailable);
        }
    }
}

public sealed class TrialRequestForm
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string GameSlug { get; set; } = "project-zomboid";
    public string? ProfileId { get; set; }
    public string? Version { get; set; }
}

public sealed class TrialConfirmationForm
{
    public string? VerificationToken { get; set; }
}

public sealed class TrialRecoveryForm
{
    public string? Email { get; set; }
}

/// <summary>Binding a form enables automatic antiforgery validation; ownership comes from the cookie.</summary>
public sealed class TrialPanelPasswordForm;
