using System.Net;
using System.Security.Cryptography;
using System.Text;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Identity;

public enum PanelPasswordSetupOutcome { Ready, DirectoryAccount, Unavailable }

public sealed record PanelPasswordSetupResult(PanelPasswordSetupOutcome Outcome, Uri? Link = null)
{
    public static PanelPasswordSetupResult Unavailable { get; } = new(PanelPasswordSetupOutcome.Unavailable);
}

/// <summary>
/// Lets a customer whose email this site has already verified choose the password used by the
/// panel's single sign-on. Staff, administrator and directory-synchronised accounts are never touched.
/// </summary>
public interface IPanelPasswordSetup
{
    bool IsConfigured { get; }

    Task<PanelPasswordSetupResult> CreateLinkAsync(string verifiedEmail, string displayName, CancellationToken cancellationToken);
}

public sealed class AuthentikPanelPasswordSetup(
    IAuthentikClient directory,
    IOptionsMonitor<AuthentikOptions> options,
    ILogger<AuthentikPanelPasswordSetup> logger) : IPanelPasswordSetup
{
    private const string CustomerType = "external";
    private const int MaxUsernameLength = 150;

    public bool IsConfigured => directory.IsConfigured;

    public async Task<PanelPasswordSetupResult> CreateLinkAsync(string verifiedEmail, string displayName,
        CancellationToken cancellationToken)
    {
        var email = TrialService.NormalizeEmail(verifiedEmail);
        if (!IsConfigured || email is null)
        {
            return PanelPasswordSetupResult.Unavailable;
        }

        try
        {
            var settings = options.CurrentValue;
            var matches = await directory.FindUsersByEmailAsync(email, cancellationToken);
            if (matches.Any(user => !IsCustomer(user, settings)))
            {
                // That person signs in with their existing directory password; never reset it from here.
                return new(PanelPasswordSetupOutcome.DirectoryAccount);
            }

            var customer = matches.Count == 0
                ? await CreateCustomerAsync(email, displayName, settings, cancellationToken)
                : matches is [var single] ? single : null;

            // However the account was resolved (found, created or recovered from a race), only an
            // active customer account with exactly this address gets a link. That excludes accounts
            // an operator disabled, superusers (e.g. a misconfigured customer group) and near matches.
            if (customer is null || !customer.IsActive || !IsCustomer(customer, settings)
                || !string.Equals(customer.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                return PanelPasswordSetupResult.Unavailable;
            }

            return new(PanelPasswordSetupOutcome.Ready, await directory.CreateRecoveryLinkAsync(customer.Pk, cancellationToken));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Panel password setup failed ({FailureClass}); the address is omitted.", error.GetType().Name);
            return PanelPasswordSetupResult.Unavailable;
        }
    }

    private async Task<AuthentikUser> CreateCustomerAsync(string email, string displayName, AuthentikOptions settings,
        CancellationToken cancellationToken)
    {
        var group = await directory.FindGroupIdAsync(settings.CustomerGroup, cancellationToken)
            ?? throw new InvalidOperationException("The configured Authentik customer group does not exist.");
        try
        {
            return await directory.CreateCustomerAsync(
                new(Username(email), Name(displayName, email), email, settings.CustomerPath, group), cancellationToken);
        }
        catch (AuthentikApiException error) when (error.StatusCode == HttpStatusCode.BadRequest)
        {
            // A concurrent request for the same customer may have created the account first.
            var raced = await directory.FindUsersByEmailAsync(email, cancellationToken);
            if (raced is [var single] && IsCustomer(single, settings))
            {
                return single;
            }
            throw;
        }
    }

    private static bool IsCustomer(AuthentikUser user, AuthentikOptions settings) =>
        !user.IsSuperuser
        && string.Equals(user.Type, CustomerType, StringComparison.Ordinal)
        && user.Groups.Contains(settings.CustomerGroup, StringComparer.Ordinal);

    private static string Username(string email) => email.Length <= MaxUsernameLength
        ? email
        : "hts-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email)))[..32];

    private static string Name(string displayName, string email)
    {
        var name = displayName.Trim();
        return name.Length is > 0 and <= 120 && !name.Any(char.IsControl) ? name : email.Split('@')[0];
    }
}

public static class PanelPasswordSetupRegistration
{
    public static IServiceCollection AddPanelPasswordSetup(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthentikOptions>().Bind(configuration.GetSection(AuthentikOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthentikOptions>, AuthentikOptionsValidator>();
        services.AddHttpClient<IAuthentikClient, AuthentikClient>(AuthentikClient.HttpClientName, (provider, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(
                    provider.GetRequiredService<IOptionsMonitor<AuthentikOptions>>().CurrentValue.TimeoutSeconds);
                client.DefaultRequestHeaders.Add("User-Agent", "HowToSoftware-Hosting/1.0");
            })
            // A redirect from the API is a login page or proxy error, never a result.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<IPanelPasswordSetup, AuthentikPanelPasswordSetup>();
        return services;
    }
}
