using System.Net;
using System.Text.RegularExpressions;
using HowToSoftware.Hosting.Endpoints;
using HowToSoftware.Hosting.Services.Subscriptions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HowToSoftware.Hosting.Tests;

public sealed class CustomerEndpointTests : IDisposable
{
    private readonly AccessFake _access = new();
    private readonly WorkspaceFake _workspace = new();
    private WebApplicationFactory<Program> Factory(string environment = "Development") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("SQLSERVER_CONNECTION_STRING", "REPLACE_ME");
            builder.UseSetting("SqlServer:ConnectionString", "");
            builder.UseSetting("ConnectionStrings:Commerce", "");
            builder.UseSetting("ConnectionStrings:Hosting", "");
            builder.UseSetting("Pterodactyl:BaseUrl", "");
            builder.UseSetting("Pterodactyl:ApiKey", "");
            builder.UseSetting("Smtp:Host", "");
            builder.UseSetting("CustomerArea:Enabled", "false");
            builder.UseSetting("CustomerArea:PreviewEnabled", "true");
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<ICustomerAccess>(); s.AddSingleton<ICustomerAccess>(_access);
                s.RemoveAll<ICustomerWorkspace>(); s.AddSingleton<ICustomerWorkspace>(_workspace);
            });
        });
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private HttpClient Client(string environment = "Development")
    {
        var factory = Factory(environment); _factories.Add(factory);
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }
    private static async Task<string> Csrf(HttpClient client, string path = "/account")
    {
        var html = await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }
    [Fact]
    public async Task OpeningEmailDoesNotLogInAndPrivatePagesAreNeverCached()
    {
        using var client = Client();
        using var response = await client.GetAsync("/account?verify=" + AccessFake.Challenge);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _access.Confirmations);
        Assert.Contains("/account/access/confirm", html);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
    [Fact]
    public async Task ConfirmationRequiresCsrfAndIssuesTransportAwareRevocableSession()
    {
        using var client = Client();
        using var rejectedContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["Token"] = AccessFake.Challenge });
        using var rejected = await client.PostAsync("/account/access/confirm", rejectedContent);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(0, _access.Confirmations);
        var csrf = await Csrf(client, "/account?verify=" + AccessFake.Challenge);
        using var acceptedContent = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Token"] = AccessFake.Challenge, ["__RequestVerificationToken"] = csrf });
        using var accepted = await client.PostAsync("/account/access/confirm", acceptedContent);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var cookie = accepted.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(CustomerSessionCookie.Name, StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/account", cookie);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        var account = await client.GetStringAsync("/account");
        Assert.Contains("owner@example.test", account);
        Assert.Equal("owner@example.test", _workspace.Owner);
        using var logoutContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = await Csrf(client) });
        using var logout = await client.PostAsync("/account/logout", logoutContent);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(1, _access.Revocations);
    }
    [Fact]
    public async Task LocalPreviewDoesNotAuthorizeFinancialOrSupportActions()
    {
        using var client = Client();
        var html = await client.GetStringAsync("/account?preview=true&tab=support");
        Assert.Contains("Local interface preview", html);
        Assert.Equal(0, _workspace.Loads);
        using var supportContent = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Subject"] = "Support", ["Message"] = "A real support request.", ["__RequestVerificationToken"] = await Csrf(client, "/account?preview=true&tab=support") });
        using var response = await client.PostAsync("/account/support", supportContent);
        Assert.Equal("/account?notice=invalid", response.Headers.Location!.ToString());
        Assert.Equal(0, _workspace.SupportCalls);
    }
    [Fact]
    public async Task ProductionAlwaysIgnoresDevelopmentPreviewFlag()
    {
        using var client = Client("Production");
        var html = await client.GetStringAsync("/account?preview=true&tab=invoices");
        Assert.DoesNotContain("Local interface preview", html);
        Assert.Contains("Send a secure sign-in link", html);
        Assert.Equal(0, _workspace.Loads);
    }
    [Fact]
    public async Task RequestEndpointHasDedicatedRateLimit()
    {
        using var client = Client();
        var token = await Csrf(client);
        HttpStatusCode last = default;
        for (var i = 0; i < 4; i++)
        {
            using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Email"] = "owner@example.test", ["__RequestVerificationToken"] = token });
            using var response = await client.PostAsync("/account/access/request", requestContent);
            last = response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
    [Fact]
    public async Task DormantExternalProvidersLeaveTheEmailFormAndExposeNoWorkingExternalLogin()
    {
        using var client = Client();
        var html = await client.GetStringAsync("/account");
        Assert.Contains("action=\"/account/access/request\"", html);
        Assert.DoesNotContain("action=\"/account/access/external\"", html);
        var csrf = await Csrf(client);
        foreach (var provider in new[] { "discord", "active-directory", "unknown" })
        {
            using var loginContent = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Provider"] = provider, ["__RequestVerificationToken"] = csrf });
            using var login = await client.PostAsync("/account/access/external", loginContent);
            Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        }
        using var callback = await client.GetAsync("/account/connections/callback/discord?code=forged&state=forged");
        Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
        Assert.Equal(0, _access.Confirmations);
    }
    [Fact]
    public async Task ConnectionsPreviewIsReadOnlyAndDoesNotAuthenticateOrReadCustomerData()
    {
        using var client = Client();
        var html = await client.GetStringAsync("/account?preview=true&tab=account");
        Assert.Contains("Company Active Directory", html);
        Assert.Contains("Discord", html);
        Assert.Contains("Not enabled", html);
        Assert.DoesNotContain("action=\"/account/connections/start\"", html);
        Assert.Equal(0, _workspace.Loads);
    }
    [Theory]
    [InlineData("/account?connection=not-a-guid")]
    [InlineData("/account/connections/return?flow=not-a-guid")]
    public async Task MalformedExternalFlowLinksDoNotBreakThePage(string path)
    {
        using var client = Client();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _access.Confirmations);
    }
    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        _factories.Clear();
    }
    private sealed class AccessFake : ICustomerAccess
    {
        public const string Challenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private const string Session = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        public bool Enabled => true;
        public int Confirmations, Revocations;
        public Task<bool> RequestAsync(string email, CancellationToken ct) => Task.FromResult(true);
        public Task<string?> ConfirmAsync(string token, CancellationToken ct) { Confirmations++; return Task.FromResult<string?>(token == Challenge ? Session : null); }
        public Task<string?> OwnerAsync(string? session, CancellationToken ct) => Task.FromResult<string?>(session == Session && Revocations == 0 ? "owner@example.test" : null);
        public Task RevokeAsync(string? session, CancellationToken ct) { Revocations++; return Task.CompletedTask; }
    }
    private sealed class WorkspaceFake : ICustomerWorkspace
    {
        public int Loads, SupportCalls;
        public string? Owner;
        public Task<CustomerWorkspaceView> LoadAsync(string owner, string tab, CancellationToken ct) { Loads++; Owner = owner; return Task.FromResult(CustomerWorkspaceView.Empty); }
        public Task<bool> SupportAsync(string owner, string subject, string message, string locale, CancellationToken ct) { SupportCalls++; Owner = owner; return Task.FromResult(true); }
        public Task DeliverPendingAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
