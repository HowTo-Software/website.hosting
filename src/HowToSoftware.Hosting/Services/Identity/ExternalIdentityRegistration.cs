using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using HowToSoftware.Hosting.Infrastructure.Security;
using HowToSoftware.Hosting.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Identity;

public static class ExternalIdentityRegistration
{
    internal const string FlowItem = "hts.external-flow";
    internal const string ProviderItem = "hts.external-provider";
    internal const string IssuerClaim = "hts:external-issuer";

    public static IServiceCollection AddExternalIdentities(this IServiceCollection services, IConfiguration config, bool database)
    {
        var configured = config.GetSection(ExternalIdentityOptions.SectionName).Get<ExternalIdentityOptions>() ?? new();
        services.AddOptions<ExternalIdentityOptions>().Bind(config.GetSection(ExternalIdentityOptions.SectionName))
            .Validate(o => !o.ActiveDirectory.LoginEnabled || o.ActiveDirectory.Enabled, "Directory login requires its provider.")
            .Validate(o => !o.Discord.LoginEnabled || o.Discord.Enabled, "Discord login requires its provider.")
            .Validate(o => (!o.ActiveDirectory.Enabled && !o.Discord.Enabled) ||
                (database && config.GetValue<bool>("CustomerArea:Enabled")
                && config.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>() is { IsConfigured: true }),
                "External identity requires the existing customer area, commerce database and email recovery.")
            .Validate(o => (!o.ActiveDirectory.Enabled && !o.Discord.Enabled) || SecureOrigin(config["Site:BaseUrl"]),
                "External identity requires a canonical HTTPS origin.")
            .Validate(o => !o.Discord.Enabled || (Snowflake(o.Discord.ClientId) && Secret(o.Discord.ClientSecret)),
                "Discord requires valid application credentials.")
            .Validate(o => !o.ActiveDirectory.Enabled,
                "The on-premises Active Directory adapter is intentionally pending. Confirm AD FS or LDAPS and implement its adapter before enabling it.")
            .ValidateOnStart();

        if (database)
        {
            services.AddScoped<ExternalIdentityService>();
            services.AddScoped<IExternalIdentityService>(p => p.GetRequiredService<ExternalIdentityService>());
            if (configured.Discord.Enabled) services.AddHostedService<ExternalIdentityCleanupWorker>();
        }
        else services.AddSingleton<IExternalIdentityService, UnavailableExternalIdentityService>();

        var authentication = services.AddAuthentication(); // Never replace the native HTS session/default scheme.
        if (configured.ActiveDirectory.Enabled || configured.Discord.Enabled)
            authentication.AddCookie(ExternalProviders.SinkScheme, options =>
            {
                options.Cookie.Name = "HTS.ExternalUnused";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Every remote ticket must be handled by the explicit HTS confirmation boundary.
                options.Events.OnSigningIn = _ => throw new InvalidOperationException("External tickets cannot create an HTS session implicitly.");
            });
        if (configured.Discord.Enabled)
            authentication.AddOAuth<OAuthOptions, DiscordIdentityHandler>(ExternalProviders.DiscordScheme, options =>
            {
                options.SignInScheme = ExternalProviders.SinkScheme;
                options.ClientId = configured.Discord.ClientId;
                options.ClientSecret = configured.Discord.ClientSecret;
                options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
                options.TokenEndpoint = "https://discord.com/api/oauth2/token";
                options.UserInformationEndpoint = "https://discord.com/api/v10/users/@me";
                options.CallbackPath = ExternalProviders.DiscordCallback;
                options.Scope.Clear(); options.Scope.Add("identify");
                options.UsePkce = true; options.SaveTokens = false;
                options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
                options.BackchannelTimeout = TimeSpan.FromSeconds(15);
                options.BackchannelHttpHandler = new HttpClientHandler { AllowAutoRedirect = false };
                options.CorrelationCookie.Name = "HTS.Discord.Correlation.";
                options.CorrelationCookie.Path = ExternalProviders.DiscordCallback;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = async context =>
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                        response.EnsureSuccessStatusCode();
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                        var id = json.RootElement.GetProperty("id").GetString();
                        if (!Snowflake(id) || (json.RootElement.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True))
                            throw new AuthenticationFailureException("External identity was rejected.");
                        context.Identity!.AddClaim(new(ClaimTypes.NameIdentifier, id!));
                        context.Identity.AddClaim(new(IssuerClaim, ExternalProviders.DiscordIssuer));
                        if (json.RootElement.TryGetProperty("username", out var name) && name.GetString() is { } display)
                            context.Identity.AddClaim(new(ClaimTypes.Name, display));
                    },
                    OnTicketReceived = RemoteTicketAsync,
                    OnRemoteFailure = RemoteFailureAsync
                };
            });
        return services;
    }

    internal static async Task RemoteTicketAsync(TicketReceivedContext context)
    {
        context.HandleResponse();
        try
        {
            var items = context.Properties?.Items;
            if (items is null) { context.Response.Redirect("/account?notice=connection-failed"); return; }
            var provider = context.Scheme.Name switch
            { ExternalProviders.DiscordScheme => ExternalProviders.Discord, ExternalProviders.DirectoryScheme => ExternalProviders.Directory, _ => "" };
            var id = items.TryGetValue(FlowItem, out var rawId) && Guid.TryParse(rawId, out var flowId) ? flowId : Guid.Empty;
            var subject = context.Principal?.FindFirstValue(provider == ExternalProviders.Directory ? "sub" : ClaimTypes.NameIdentifier);
            var issuer = context.Principal?.FindFirstValue(provider == ExternalProviders.Directory ? "iss" : IssuerClaim);
            if (!items.TryGetValue(ProviderItem, out var expectedProvider) || provider != expectedProvider || id == Guid.Empty || subject is null || issuer is null
                || !await context.HttpContext.RequestServices.GetRequiredService<ExternalIdentityService>()
                    .VerifyAsync(id, provider, issuer, subject, context.Principal?.FindFirstValue("name")
                        ?? context.Principal?.FindFirstValue(ClaimTypes.Name), context.HttpContext.RequestAborted))
            { context.Response.Redirect("/account?notice=connection-failed"); return; }
            // Final CSRF-protected POST checks the original browser and (for linking) HTS session.
            context.Response.Redirect("/account/connections/return?flow=" + id.ToString("N"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailure(context.HttpContext, e);
            context.Response.Redirect("/account?notice=connection-failed");
        }
    }
    internal static Task RemoteFailureAsync(RemoteFailureContext context)
    {
        context.HandleResponse();
        // Do not log the exception object: provider error bodies can contain tokens/PII.
        if (context.Failure is { } error) LogFailure(context.HttpContext, error);
        context.Response.Redirect("/account?notice=connection-failed");
        return Task.CompletedTask;
    }
    internal static void LogFailure(HttpContext context, Exception error) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ExternalIdentity")
            .LogWarning("External identity operation was rejected ({FailureClass}).", error.GetType().Name);
    internal static bool Snowflake(string? value) => value is { Length: >= 17 and <= 20 }
        && value[0] != '0' && value.All(char.IsAsciiDigit) && ulong.TryParse(value, out var id) && id > 0;
    private static bool Secret(string value) => value.Length >= 16 && !value.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("_AQUI", StringComparison.OrdinalIgnoreCase) && !value.Any(char.IsControl);
    private static bool SecureOrigin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
