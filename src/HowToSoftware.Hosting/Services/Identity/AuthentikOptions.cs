using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Identity;

/// <summary>
/// Backend-only access to the Authentik directory behind the game panel's single sign-on.
/// Website customers are created there so they can choose the password the panel signs in with.
/// </summary>
public sealed class AuthentikOptions
{
    public const string SectionName = "Authentik";

    /// <summary>Public HTTPS origin, e.g. https://auth.howto.software. One-time links point here.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Token of a service account that can view/create users and create recovery links.</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Existing group that marks website customers; the panel uses the same name.</summary>
    public string CustomerGroup { get; set; } = "hts-customers";

    /// <summary>Directory path given to customers created by this site.</summary>
    public string CustomerPath { get; set; } = "customers/hts";

    public int TimeoutSeconds { get; set; } = 15;

    public bool IsConfigured => Origin is not null
        && !string.IsNullOrWhiteSpace(ApiToken)
        && !ApiToken.Contains("_AQUI", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CustomerGroup);

    /// <summary>The scheme and authority of <see cref="BaseUrl"/>, when it is an HTTPS root URL.</summary>
    public Uri? Origin =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && uri.UserInfo.Length == 0
        && !uri.Host.Contains("_AQUI", StringComparison.OrdinalIgnoreCase)
            ? new Uri(uri.GetLeftPart(UriPartial.Authority))
            : null;
}

public sealed class AuthentikOptionsValidator : IValidateOptions<AuthentikOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthentikOptions options)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(options.BaseUrl);
        var hasToken = !string.IsNullOrWhiteSpace(options.ApiToken);
        if (!hasUrl && !hasToken)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (hasUrl != hasToken)
        {
            failures.Add("Authentik:BaseUrl and Authentik:ApiToken must either both be configured or both be omitted.");
        }

        if (hasUrl && options.Origin is null)
        {
            failures.Add("Authentik:BaseUrl must be an HTTPS origin such as https://auth.example.com, without a path, query or credentials.");
        }

        if (string.IsNullOrWhiteSpace(options.CustomerGroup) || options.CustomerGroup.Length > 80
            || options.CustomerGroup.Any(char.IsControl))
        {
            failures.Add("Authentik:CustomerGroup must name the existing customer group in 1-80 characters.");
        }

        if (options.CustomerPath is not { Length: > 0 and <= 200 } path || path.StartsWith('/') || path.EndsWith('/')
            || path.Contains("//", StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            failures.Add("Authentik:CustomerPath must be a relative path without leading, trailing or empty segments.");
        }

        if (options.TimeoutSeconds is < 2 or > 60)
        {
            failures.Add("Authentik:TimeoutSeconds must be between 2 and 60.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
