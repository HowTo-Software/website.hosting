using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Identity;

/// <summary>A directory account as Authentik reports it; only the fields this site relies on.</summary>
public sealed record AuthentikUser(int Pk, string Username, string Email, bool IsActive, bool IsSuperuser,
    string Type, IReadOnlyList<string> Groups);

/// <summary>A website customer to create. Authentik's API cannot set passwords; recovery links do.</summary>
public sealed record AuthentikNewCustomer(string Username, string Name, string Email, string Path, string GroupId);

/// <summary>Never carries the token, an email address or Authentik's response body.</summary>
public sealed class AuthentikApiException(HttpStatusCode? statusCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public interface IAuthentikClient
{
    bool IsConfigured { get; }

    /// <summary>Accounts whose email or username is exactly this address, ignoring case.</summary>
    Task<IReadOnlyList<AuthentikUser>> FindUsersByEmailAsync(string email, CancellationToken cancellationToken);

    Task<string?> FindGroupIdAsync(string name, CancellationToken cancellationToken);

    Task<AuthentikUser> CreateCustomerAsync(AuthentikNewCustomer customer, CancellationToken cancellationToken);

    /// <summary>A one-time link into the brand's recovery flow, where the user chooses a password.</summary>
    Task<Uri> CreateRecoveryLinkAsync(int userPk, CancellationToken cancellationToken);
}

/// <summary>HTTP client for the Authentik core API. The token is attached per request from options.</summary>
public sealed class AuthentikClient(HttpClient http, IOptionsMonitor<AuthentikOptions> options) : IAuthentikClient
{
    public const string HttpClientName = "authentik";

    private const string ApiPath = "/api/v3";

    // 1,000 accounts containing one customer's address means something is wrong; refuse instead.
    private const int MaxSearchPages = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task<IReadOnlyList<AuthentikUser>> FindUsersByEmailAsync(string email, CancellationToken cancellationToken)
    {
        // Search is a case-insensitive substring match across several fields. Every page is read, so
        // an exact match or a conflicting staff account is never missed; exactness is enforced here.
        var matches = new List<AuthentikUser>();
        for (var page = 1; page <= MaxSearchPages; page++)
        {
            var result = await SendAsync<AuthentikPage<AuthentikUserBody>>(HttpMethod.Get,
                $"/core/users/?search={Uri.EscapeDataString(email)}&page={page}&page_size=100", null, cancellationToken)
                .ConfigureAwait(false);
            matches.AddRange((result.Results ?? [])
                .Where(user => string.Equals(user.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(user.Username, email, StringComparison.OrdinalIgnoreCase))
                .Select(Map));
            if (result.Pagination is not { Next: > 0 })
            {
                return matches;
            }
        }

        // Deciding on a partial view of the directory could miss a conflicting account.
        throw new AuthentikApiException(null, "Authentik returned more search results than can be checked.");
    }

    public async Task<string?> FindGroupIdAsync(string name, CancellationToken cancellationToken)
    {
        var page = await SendAsync<AuthentikPage<AuthentikGroupBody>>(HttpMethod.Get,
            $"/core/groups/?name={Uri.EscapeDataString(name)}&include_users=false&page_size=20", null, cancellationToken)
            .ConfigureAwait(false);
        var matches = (page.Results ?? []).Where(group => string.Equals(group.Name, name, StringComparison.Ordinal)).ToArray();
        return matches is [var group] ? group.Pk : null;
    }

    public async Task<AuthentikUser> CreateCustomerAsync(AuthentikNewCustomer customer, CancellationToken cancellationToken)
    {
        var created = await SendAsync<AuthentikUserBody>(HttpMethod.Post, "/core/users/", new AuthentikCreateUserBody(
            customer.Username, customer.Name, IsActive: true, customer.Email, [customer.GroupId], customer.Path,
            Type: "external", new Dictionary<string, object> { ["hts_customer"] = true }), cancellationToken).ConfigureAwait(false);
        return Map(created);
    }

    public async Task<Uri> CreateRecoveryLinkAsync(int userPk, CancellationToken cancellationToken)
    {
        var path = $"/core/users/{userPk}/recovery/";
        using var response = await ExecuteAsync(HttpMethod.Post, path, new { }, cancellationToken).ConfigureAwait(false);
        // Current Authentik creates the link with POST; releases before that exposed the same action as GET.
        var body = response.StatusCode == HttpStatusCode.MethodNotAllowed
            ? await SendAsync<AuthentikLinkBody>(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false)
            : await ReadAsync<AuthentikLinkBody>(response, cancellationToken).ConfigureAwait(false);
        return EnsureOwnLink(body.Link);
    }

    private Uri EnsureOwnLink(string? link)
    {
        // The browser is redirected to this link, so it must stay on the configured Authentik origin.
        if (options.CurrentValue.Origin is not { } origin
            || !Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthentikApiException(null, "Authentik returned a recovery link outside its configured origin.");
        }
        return uri;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        using var response = await ExecuteAsync(method, path, payload, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> ExecuteAsync(HttpMethod method, string path, object? payload,
        CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.IsConfigured || settings.Origin is not { } origin)
        {
            throw new AuthentikApiException(null, "Authentik is not configured.");
        }

        using var request = new HttpRequestMessage(method, new Uri(origin, ApiPath + path));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.ApiToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, payload.GetType(), options: JsonOptions);
        }

        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthentikApiException(null, "Authentik did not respond in time.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AuthentikApiException(null, "Authentik could not be reached.", exception);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Error bodies can repeat submitted usernames or addresses, so they are not read.
            throw new AuthentikApiException(response.StatusCode,
                $"Authentik rejected the request with HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new AuthentikApiException(response.StatusCode, "Authentik returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new AuthentikApiException(response.StatusCode, "Authentik returned an unreadable response.", exception);
        }
    }

    private static AuthentikUser Map(AuthentikUserBody user) => new(user.Pk, user.Username, user.Email?.Trim() ?? string.Empty,
        user.IsActive, user.IsSuperuser, user.Type ?? string.Empty,
        user.GroupsObj?.Select(group => group.Name).ToArray() ?? []);
}

internal sealed record AuthentikPage<T>(IReadOnlyList<T>? Results, AuthentikPagination? Pagination = null);

/// <summary>Authentik reports the next page number, or 0 on the last page.</summary>
internal sealed record AuthentikPagination(int Next);

internal sealed record AuthentikGroupBody(string Pk, string Name);

internal sealed record AuthentikUserBody(int Pk, string Username, string? Email, bool IsActive, bool IsSuperuser,
    string? Type, IReadOnlyList<AuthentikGroupBody>? GroupsObj);

internal sealed record AuthentikCreateUserBody(string Username, string Name, bool IsActive, string Email,
    IReadOnlyList<string> Groups, string Path, string Type, IReadOnlyDictionary<string, object> Attributes);

internal sealed record AuthentikLinkBody(string? Link);
