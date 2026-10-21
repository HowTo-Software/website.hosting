using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Endpoints;
using HowToSoftware.Hosting.Infrastructure.Security;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Identity;
using HowToSoftware.Hosting.Services.Subscriptions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

/// <summary>Real OAuth/correlation/PKCE middleware against an in-memory, fake Discord backchannel.</summary>
public sealed class DiscordIdentityProtocolTests
{
    [Fact]
    public async Task RealOAuthFlowRequiresCsrfPkceCorrelationBrowserProofAndFinalConfirmation()
    {
        await using var h = await Harness.Create();
        using var noCsrf = await h.Post("/account/access/external", new() { ["Provider"] = "discord" });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var challenge = await h.Start();
        var query = QueryHelpers.ParseQuery(challenge.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://localhost" + ExternalProviders.DiscordCallback, query["redirect_uri"]);
        Assert.False(query.ContainsKey("email"));
        var state = query["state"].ToString();
        using var callback = await h.Callback(state, ExternalProviders.DiscordIssuer);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.StartsWith("/account/connections/return?flow=", callback.Headers.Location!.OriginalString);
        Assert.DoesNotContain(h.Cookies.Keys, name => name == CustomerSessionCookie.Name);
        Assert.Equal(2, h.Backchannel.Requests);
        var tokenRequest = QueryHelpers.ParseQuery("?" + h.Backchannel.TokenBody);
        Assert.Equal("FAKE_CODE_FOR_TEST", tokenRequest["code"]);
        var verifier = tokenRequest["code_verifier"].ToString();
        var expectedChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(query["code_challenge"], expectedChallenge);
        var flow = QueryHelpers.ParseQuery(new Uri(h.Client.BaseAddress!, callback.Headers.Location).Query)["flow"].ToString();
        var result = await h.Post("/account/connections/confirm", new() { ["FlowId"] = flow, ["__RequestVerificationToken"] = h.Csrf });
        Assert.Equal("/account?tab=account&notice=connection-complete", result.Headers.Location!.OriginalString);
        Assert.True(h.Cookies.ContainsKey(CustomerSessionCookie.Name));
        await using var db = h.Factory.CreateDbContext();
        var owner = await db.CustomerAccess.SingleAsync();
        Assert.Equal("actual-owner@example.test", owner.NormalizedEmail); // Ignore Discord's forged email/role.
        Assert.Equal(CustomerAccessService.Hash(h.Cookies[CustomerSessionCookie.Name]), owner.SessionHash);
        Assert.Equal(1, await db.CustomerExternalIdentities.CountAsync());
        var authentication = h.Server.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Null(await authentication.GetDefaultAuthenticateSchemeAsync());
    }
    [Theory]
    [InlineData("https://attacker.test")]
    [InlineData("")]
    public async Task MissingOrWrongAuthorizationIssuerIsRejectedBeforeExchangingTheCode(string issuer)
    {
        await using var h = await Harness.Create();
        var challenge = await h.Start();
        var state = QueryHelpers.ParseQuery(challenge.Query)["state"].ToString();
        using var response = await h.Callback(state, issuer);
        Assert.Equal("/account?notice=connection-failed", response.Headers.Location!.OriginalString);
        Assert.Equal(0, h.Backchannel.Requests);
        Assert.False(h.Cookies.ContainsKey(CustomerSessionCookie.Name));
    }
    [Fact]
    public async Task InvalidStateNeverContactsDiscord()
    {
        await using var h = await Harness.Create();
        await h.Start();
        using var response = await h.Callback("forged-state", ExternalProviders.DiscordIssuer);
        Assert.Equal("/account?notice=connection-failed", response.Headers.Location!.OriginalString);
        Assert.Equal(0, h.Backchannel.Requests);
    }
    [Fact]
    public async Task ACallbackWithoutItsCorrelationCookieCannotVerifyAnIdentity()
    {
        await using var h = await Harness.Create();
        var challenge = await h.Start();
        var state = QueryHelpers.ParseQuery(challenge.Query)["state"].ToString();
        foreach (var name in h.Cookies.Keys.Where(x => x.StartsWith("HTS.Discord.Correlation.", StringComparison.Ordinal)).ToArray())
            h.Cookies.Remove(name);
        using var response = await h.Callback(state, ExternalProviders.DiscordIssuer);
        Assert.Equal("/account?notice=connection-failed", response.Headers.Location!.OriginalString);
        Assert.Equal(0, h.Backchannel.Requests);
    }
    [Fact]
    public async Task CompletedProviderProofWithoutTheOriginalBrowserCookieCannotSignIn()
    {
        await using var h = await Harness.Create();
        var challenge = await h.Start();
        var state = QueryHelpers.ParseQuery(challenge.Query)["state"].ToString();
        using var callback = await h.Callback(state, ExternalProviders.DiscordIssuer);
        var flow = QueryHelpers.ParseQuery(new Uri(h.Client.BaseAddress!, callback.Headers.Location!).Query)["flow"].ToString();
        h.Cookies.Remove(ExternalFlowCookie.Name);
        using var response = await h.Post("/account/connections/confirm", new() { ["FlowId"] = flow, ["__RequestVerificationToken"] = h.Csrf });
        Assert.Equal("/account?notice=connection-failed", response.Headers.Location!.OriginalString);
        Assert.False(h.Cookies.ContainsKey(CustomerSessionCookie.Name));
    }
    [Fact]
    public async Task ProviderErrorBodiesNeverBecomePublicResponses()
    {
        await using var h = await Harness.Create();
        h.Backchannel.FailToken = true;
        var challenge = await h.Start();
        using var response = await h.Callback(QueryHelpers.ParseQuery(challenge.Query)["state"].ToString(), ExternalProviders.DiscordIssuer);
        Assert.Equal("/account?notice=connection-failed", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("DO_NOT_EXPOSE_TEST_TOKEN", await response.Content.ReadAsStringAsync());
        Assert.False(h.Cookies.ContainsKey(CustomerSessionCookie.Name));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private SqliteConnection _connection = new("Data Source=:memory:");
        public TestServer Server = null!;
        private IHost _host = null!;
        public HttpClient Client = null!;
        public TestFactory Factory = null!;
        public FakeDiscord Backchannel = new();
        public Dictionary<string, string> Cookies = [];
        public string Csrf = "";
        public static async Task<Harness> Create()
        {
            var h = new Harness(); await h._connection.OpenAsync();
            h._connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            h.Factory = new(new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(h._connection).Options);
            await using (var db = h.Factory.CreateDbContext())
            {
                await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT", StringComparison.OrdinalIgnoreCase));
                var owner = Guid.NewGuid();
                db.CustomerAccess.Add(new() { Id = owner, NormalizedEmail = "actual-owner@example.test",
                    ChallengeHash = CustomerAccessService.Hash(CustomerAccessService.NewToken()), CreatedAt = DateTimeOffset.UtcNow });
                db.CustomerExternalIdentities.Add(new() { Id = Guid.NewGuid(), CustomerAccessId = owner, Provider = "discord",
                    Issuer = ExternalProviders.DiscordIssuer, Subject = "123456789012345678", DisplayName = "Linked Discord", LinkedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Site:BaseUrl"] = "https://localhost", ["CustomerArea:Enabled"] = "true",
                ["Smtp:Host"] = "smtp.invalid", ["Smtp:FromAddress"] = "sender@example.test",
                ["ExternalIdentity:Discord:Enabled"] = "true", ["ExternalIdentity:Discord:LoginEnabled"] = "true",
                ["ExternalIdentity:Discord:ClientId"] = "123456789012345678",
                ["ExternalIdentity:Discord:ClientSecret"] = "DISCORD_FAKE_TEST_SECRET_NOT_REAL"
            }).Build();
            var builder = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().UseEnvironment("Development").ConfigureServices(s =>
            {
                s.AddLogging(o => o.ClearProviders());
                s.AddRouting(); s.AddDataProtection();
                s.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
                s.AddSiteSecurity(new TestEnvironment(), config);
                s.AddSingleton(TimeProvider.System);
                s.AddSingleton<IDbContextFactory<CommerceDbContext>>(h.Factory);
                s.AddSingleton<ICustomerAccess, NativeAccessEnabled>();
                s.Configure<SiteOptions>(o => o.BaseUrl = "https://localhost");
                s.AddExternalIdentities(config, database: true);
                s.Configure<OAuthOptions>(ExternalProviders.DiscordScheme, o => o.BackchannelHttpHandler = h.Backchannel);
            }).Configure(app =>
            {
                app.UseRouting(); app.UseSiteSecurityHeaders(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAntiforgery();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/form", (IAntiforgery csrf, HttpContext context) => Results.Text(csrf.GetAndStoreTokens(context).RequestToken!));
                    endpoints.MapExternalIdentityEndpoints();
                });
            }));
            h._host = await builder.StartAsync();
            h.Server = h._host.GetTestServer();
            h.Client = h.Server.CreateClient(); h.Client.BaseAddress = new("https://localhost");
            using var formRequest = new HttpRequestMessage(HttpMethod.Get, "/form");
            using var response = await h.Send(formRequest);
            h.Csrf = await response.Content.ReadAsStringAsync();
            return h;
        }
        public async Task<Uri> Start()
        {
            using var response = await Post("/account/access/external", new() { ["Provider"] = "discord", ["__RequestVerificationToken"] = Csrf });
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return response.Headers.Location!;
        }
        public Task<HttpResponseMessage> Callback(string state, string issuer) => Send(new(HttpMethod.Get,
            ExternalProviders.DiscordCallback + "?state=" + Uri.EscapeDataString(state) + "&code=FAKE_CODE_FOR_TEST&iss=" + Uri.EscapeDataString(issuer)));
        public Task<HttpResponseMessage> Post(string path, Dictionary<string, string> fields) =>
            Send(new(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) });
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            using var _ = request;
            if (Cookies.Count != 0) request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(x => x.Key + "=" + x.Value)));
            var response = await Client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values)
                {
                    var pair = value.Split(';')[0].Split('=', 2);
                    if (pair.Length == 2 && pair[1].Length != 0) Cookies[pair[0]] = pair[1];
                    else Cookies.Remove(pair[0]);
                }
            return response;
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await _host.StopAsync(); _host.Dispose(); Backchannel.Dispose(); await _connection.DisposeAsync(); }
    }
    private sealed class FakeDiscord : HttpMessageHandler
    {
        public int Requests;
        public bool FailToken;
        public string? TokenBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Method == HttpMethod.Post)
            {
                TokenBody = await request.Content!.ReadAsStringAsync(ct);
                return new(FailToken ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
                { Content = new StringContent(FailToken ? "{\"error\":\"invalid_grant\",\"error_description\":\"DO_NOT_EXPOSE_TEST_TOKEN\"}" : "{\"access_token\":\"FAKE_TEST_ACCESS_TOKEN\",\"token_type\":\"Bearer\",\"expires_in\":3600}") };
            }
            Assert.Equal("https://discord.com/api/v10/users/@me", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"123456789012345678\",\"username\":\"Test Discord\",\"email\":\"wrong-owner@example.test\",\"roles\":[\"Administrator\"]}") };
        }
    }
    private sealed class TestFactory(DbContextOptions<CommerceDbContext> options) : IDbContextFactory<CommerceDbContext>
    { public CommerceDbContext CreateDbContext() => new(options); }
    private sealed class NativeAccessEnabled : ICustomerAccess
    {
        public bool Enabled => true;
        public Task<bool> RequestAsync(string email, CancellationToken ct) => throw new InvalidOperationException();
        public Task<string?> ConfirmAsync(string token, CancellationToken ct) => throw new InvalidOperationException();
        public Task<string?> OwnerAsync(string? session, CancellationToken ct) => throw new InvalidOperationException();
        public Task RevokeAsync(string? session, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "HowToSoftware.Hosting.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
