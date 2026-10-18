using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Net.Http.Headers;

namespace HowToSoftware.Hosting.Tests;

/// <summary>Production transport and host boundaries without SQL, mail, Stripe or panel calls.</summary>
public sealed class ProductionReadinessTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("SQLSERVER_CONNECTION_STRING", "REPLACE_ME");
            builder.UseSetting("SqlServer:ConnectionString", string.Empty);
            builder.UseSetting("ConnectionStrings:Commerce", string.Empty);
            builder.UseSetting("ConnectionStrings:Hosting", string.Empty);
            builder.UseSetting("Pterodactyl:BaseUrl", string.Empty);
            builder.UseSetting("Pterodactyl:ApiKey", string.Empty);
            builder.UseSetting("Trials:Enabled", "false");
            builder.UseSetting("Smtp:Host", string.Empty);
        });

    [Theory]
    [InlineData("http", false)]
    [InlineData("https", true)]
    public async Task ProductionAntiforgeryCookieIsBrowserValidAndProtectsFormPosts(string scheme, bool secure)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"{scheme}://host.howto.software"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        using var page = await client.GetAsync("/trial");
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(tokenMatch.Success);
        var cookie = Assert.Single(page.Headers.GetValues("Set-Cookie")
            .Select(value => SetCookieHeaderValue.Parse(value)), value => value.Name.ToString().Contains("Antiforgery"));

        Assert.Equal(secure, cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.False(cookie.Domain.HasValue);
        Assert.Equal("/", cookie.Path.ToString());
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.True(!cookie.Name.ToString().StartsWith("__Host-", StringComparison.Ordinal) || cookie.Secure,
            "Browsers reject __Host- cookies without Secure, even when the server accepts the POST.");

        var form = new Dictionary<string, string>
        {
            ["Email"] = "player@example.test",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value)
        };
        using var noCookie = await client.PostAsync("/trials/recover", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, noCookie.StatusCode);

        using var validPost = new HttpRequestMessage(HttpMethod.Post, "/trials/recover")
        {
            Content = new FormUrlEncodedContent(form)
        };
        validPost.Headers.Add("Cookie", $"{cookie.Name}={cookie.Value}");
        using var accepted = await client.SendAsync(validPost);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal("/trial?notice=unavailable", accepted.Headers.Location?.ToString());
    }

    [Fact]
    public async Task TheShippedHostAllowlistAcceptsTheProductionDomainAndRejectsUnknownHosts()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://host.howto.software"),
            AllowAutoRedirect = false
        });
        using var allowed = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "/");
        spoofed.Headers.Host = "untrusted.example";
        using var rejected = await client.SendAsync(spoofed);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("203.0.113.10", false)]
    public async Task OnlyTrustedProxiesCanChangeTheSecurityTransport(string proxyAddress, bool trusted)
    {
        // Initialise the in-process server, then control the actual peer IP rather than
        // pretending that an arbitrary request header proves which proxy sent the request.
        using var client = _factory.CreateClient();
        var response = await _factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(proxyAddress);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("host.howto.software");
            context.Request.Path = "/";
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.20";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var csp = response.Response.Headers.ContentSecurityPolicy.ToString();
        Assert.Equal(trusted, csp.Contains("upgrade-insecure-requests", StringComparison.Ordinal));
        Assert.Equal(trusted, response.Response.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.Contains(trusted ? "wss://host.howto.software" : "ws://host.howto.software", csp);
    }

    public void Dispose() => _factory.Dispose();
}
