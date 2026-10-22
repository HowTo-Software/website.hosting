using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;
using HowToSoftware.Hosting.Services.Identity;

namespace HowToSoftware.Hosting.Infrastructure.Security;

/// <summary>Reject authorization-server mix-up BEFORE transmitting the authorization code.</summary>
public sealed class DiscordIdentityHandler(IOptionsMonitor<OAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : OAuthHandler<OAuthOptions>(options, logger, encoder)
{
    protected override async Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
    {
        try
        {
            var result = await base.HandleRemoteAuthenticateAsync();
            return result.Failure is null ? result : Rejected();
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested) { throw; }
        catch (AuthenticationFailureException) { return Rejected(); }
        catch (HttpRequestException) { return Rejected(); }
        catch (TaskCanceledException) when (!Context.RequestAborted.IsCancellationRequested) { return Rejected(); }
        catch (InvalidOperationException) { return Rejected(); } // Provider bodies/codes/tokens must not become public error/log details.
    }

    private static HandleRequestResult Rejected() =>
        HandleRequestResult.Fail(new AuthenticationFailureException("External provider response was rejected."));

    protected override Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        if (Request.Query["iss"].Count != 1 || Request.Query["iss"][0] != ExternalProviders.DiscordIssuer)
            return Task.FromResult(OAuthTokenResponse.Failed(new AuthenticationFailureException("External provider response was rejected.")));
        return base.ExchangeCodeAsync(context);
    }
}
