using System.Net;
using System.Text.RegularExpressions;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Orders;
using HowToSoftware.Hosting.Services.Identity;
using HowToSoftware.Hosting.Services.Payments;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stripe;
using Stripe.Checkout;

namespace HowToSoftware.Hosting.Tests;

/// <summary>Real native form/CSRF/cookie boundaries; no SQL, mail or panel calls.</summary>
public sealed class TrialEndpointTests : IDisposable
{
    private readonly RecordingTrialService _trials = new();
    private readonly RecordingCheckoutService _checkout = new();
    private readonly RecordingPanelPasswords _passwords = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TrialEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("SQLSERVER_CONNECTION_STRING", "REPLACE_ME");
            builder.UseSetting("ConnectionStrings:Hosting", string.Empty);
            // Empty panel options are a valid unconfigured host. A placeholder key without
            // a URL is intentionally rejected when another startup validator reads them.
            builder.UseSetting("Pterodactyl:BaseUrl", string.Empty);
            builder.UseSetting("Pterodactyl:ApiKey", string.Empty);
            builder.UseSetting("Smtp:Host", string.Empty);
            builder.UseSetting("Authentik:BaseUrl", "https://auth.example.test");
            builder.UseSetting("Authentik:ApiToken", "unit-test-only");
            builder.ConfigureServices(services =>
            {
                // Apply after configuration binding as well, so inherited environment aliases
                // cannot turn this endpoint-only fixture into a configured panel host.
                services.Configure<PterodactylOptions>(settings =>
                {
                    settings.BaseUrl = string.Empty;
                    settings.ApiKey = string.Empty;
                    settings.TimeoutSeconds = 30;
                });
                services.RemoveAll<ITrialService>();
                services.AddSingleton<ITrialService>(_trials);
                services.RemoveAll<IStripeCheckoutService>();
                services.AddSingleton<IStripeCheckoutService>(_checkout);
                services.RemoveAll<IPanelPasswordSetup>();
                services.AddSingleton<IPanelPasswordSetup>(_passwords);
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
        });
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
    }

    public void Dispose() { _client.Dispose(); _factory.Dispose(); }

    private async Task<string> CsrfAsync(string path = "/trial")
    {
        using var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, "A real antiforgery form field must be rendered.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Fact]
    public async Task OpeningAnEmailLinkNeverConfirmsOrProvisionsAnything()
    {
        using var response = await _client.GetAsync("/trial?verify=unit-verification-token");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/trials/confirm\"", html);
        Assert.Contains("Confirm my email", html);
        Assert.Equal(0, _trials.ConfirmCalls);
        Assert.Empty(_trials.Requests);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/trials/request")]
    [InlineData("/trials/confirm")]
    [InlineData("/trials/recover")]
    [InlineData("/trials/panel-password")]
    public async Task NativeMutationPostsRequireAntiforgery(string path)
    {
        _client.DefaultRequestHeaders.Add("Cookie", "hts.trial-access=unit-owner-cookie");
        _trials.Account = Account(TrialState.Active);
        using var response = await _client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "player@example.test", ["DisplayName"] = "Player", ["GameSlug"] = "project-zomboid",
            ["VerificationToken"] = "unit-verification-token"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_trials.Requests);
        Assert.Equal(0, _trials.ConfirmCalls);
        Assert.Equal(0, _trials.RecoveryCalls);
        Assert.Empty(_passwords.Requests);
    }

    [Fact]
    public async Task AVerifiedPostStoresAnOpaqueOwnerTokenInASecureHttpOnlyCookie()
    {
        var token = await CsrfAsync("/trial?verify=unit-verification-token");
        using var response = await _client.PostAsync("/trials/confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["VerificationToken"] = "unit-verification-token", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/trial", response.Headers.Location?.ToString());
        Assert.Equal(1, _trials.ConfirmCalls);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("hts.trial-access="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("player@example.test", cookie);
        Assert.DoesNotContain("unit-owner-token", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task TrialRequestPostsOnlyAnApprovedSelectionAndNeverAPriceOrResourceLimit()
    {
        var token = await CsrfAsync();
        using var response = await _client.PostAsync("/trials/request", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "player@example.test", ["DisplayName"] = "Player", ["GameSlug"] = "project-zomboid",
            ["MemoryMb"] = "999999", ["CpuPercent"] = "999999", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/trial?notice=sent", response.Headers.Location?.ToString());
        var request = Assert.Single(_trials.Requests);
        Assert.Equal("player@example.test", request.Email);
        Assert.Equal("project-zomboid", request.GameSlug);
        Assert.Equal("en", request.Locale);
        Assert.DoesNotContain("player@example.test", response.Headers.Location?.ToString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task OwnerRecoveryRemainsAvailableWhenNewTrialsOrAnEditionAreUnavailable()
    {
        _trials.IsAvailable = false;
        var token = await CsrfAsync("/trial?game=minecraft&edition=bedrock");
        using var response = await _client.PostAsync("/trials/recover", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "player@example.test", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/trial?notice=sent", response.Headers.Location?.ToString());
        Assert.Equal(1, _trials.RecoveryCalls);
        Assert.Empty(_trials.Requests);
    }

    [Fact]
    public async Task AnUnconfiguredMinecraftEditionHasNoFakeSoftwareOrEnabledSubmit()
    {
        using var response = await _client.GetAsync("/trial?game=minecraft&edition=bedrock");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("This edition has no trial software available yet", html);
        Assert.DoesNotContain("Forge", html);
        Assert.DoesNotContain("Paper", html);
        Assert.Matches("<button[^>]*disabled[^>]*>", html);
    }

    private static TrialAccountView Account(TrialState state, Guid? pendingOrder = null) => new(
        Guid.NewGuid(), "owner@example.test", "Owner", "project-zomboid", null, null, state, 77, "trial77",
        DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(22), null,
        state is TrialState.Active or TrialState.Expired && pendingOrder is null, pendingOrder);

    private static FormUrlEncodedContent CsrfForm(string token) =>
        new(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });

    [Fact]
    public async Task AVerifiedOwnerIsHandedToAOneTimeAuthentikPasswordPage()
    {
        _trials.Account = Account(TrialState.Active);
        _client.DefaultRequestHeaders.Add("Cookie", "hts.trial-access=unit-owner-cookie");
        using (var page = await _client.GetAsync("/trial"))
        {
            Assert.Contains("action=\"/trials/panel-password\"", await page.Content.ReadAsStringAsync());
            // The POST is redirected to Authentik, which browsers check against form-action.
            Assert.Contains("form-action 'self' https://auth.example.test;",
                Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        }

        using var content = CsrfForm(await CsrfAsync());
        using var response = await _client.PostAsync("/trials/panel-password", content);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(RecordingPanelPasswords.Link, response.Headers.Location);
        Assert.Equal(("owner@example.test", "Owner"), Assert.Single(_passwords.Requests));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(TrialState.Installing, true)]
    [InlineData(TrialState.Converted, true)]
    [InlineData(TrialState.Deleted, false)]
    [InlineData(TrialState.Rejected, false)]
    public async Task PasswordSetupIsOfferedOnlyWhileTheOwnerHasAPanelAccount(TrialState state, bool offered)
    {
        _trials.Account = Account(state);
        _client.DefaultRequestHeaders.Add("Cookie", "hts.trial-access=unit-owner-cookie");
        using var page = await _client.GetAsync("/trial");
        Assert.Equal(offered, (await page.Content.ReadAsStringAsync()).Contains("action=\"/trials/panel-password\""));
    }

    [Fact]
    public async Task WithoutTheOwnerCookieNoPasswordLinkIsCreated()
    {
        using var content = CsrfForm(await CsrfAsync());
        using var response = await _client.PostAsync("/trials/panel-password", content);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/trial?notice=panel-password-unavailable", response.Headers.Location?.ToString());
        Assert.Empty(_passwords.Requests);
    }

    [Fact]
    public async Task AStaffDirectoryAccountKeepsItsPasswordAndTheOwnerIsToldWhy()
    {
        _trials.Account = Account(TrialState.Active);
        _passwords.Outcome = PanelPasswordSetupOutcome.DirectoryAccount;
        _client.DefaultRequestHeaders.Add("Cookie", "hts.trial-access=unit-owner-cookie");
        using var content = CsrfForm(await CsrfAsync());
        using var response = await _client.PostAsync("/trials/panel-password", content);
        Assert.Equal("/trial?notice=panel-directory", response.Headers.Location?.ToString());

        // The owner view replaces the request form, so the notice has to be rendered there.
        using var page = await _client.GetAsync("/trial?notice=panel-directory");
        Assert.Contains("This email already has an HTS staff sign-in", await page.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(TrialState.Active, "true")]
    [InlineData(TrialState.Expired, "true")]
    [InlineData(TrialState.Converted, "false")]
    [InlineData(TrialState.Deleted, "false")]
    public async Task ReviewKeepsUpgradeIntentOnlyForAnExistingEligibleTrial(TrialState state, string expectedIntent)
    {
        _trials.Account = Account(state);
        _client.DefaultRequestHeaders.Add("Cookie", "hts.trial-access=unit-owner-cookie");
        using var response = await _client.GetAsync("/game-hosting/project-zomboid/review?plan=zomboid-4gb&period=monthly");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"name=\"TrialUpgrade\" value=\"{expectedIntent}\"", html);
        if (expectedIntent == "true") Assert.Contains("This plan continues on your trial server", html);
        else Assert.DoesNotContain("This plan continues on your trial server", html);
    }

    [Fact]
    public async Task ATrialWithPendingCheckoutCannotSilentlyBecomeAFreshServerPurchase()
    {
        _trials.Account = Account(TrialState.Active, Guid.NewGuid());
        _client.DefaultRequestHeaders.Add("Cookie", "hts.trial-access=unit-owner-cookie");
        using var response = await _client.GetAsync("/game-hosting/project-zomboid/review?plan=zomboid-4gb&period=monthly");
        Assert.Contains("name=\"TrialUpgrade\" value=\"true\"", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APostedUpgradeRetainsItsIntentWhenEligibilityOrTheOwnerCookieChanges(bool keepCookie)
    {
        const string review = "/game-hosting/project-zomboid/review?plan=zomboid-4gb&period=monthly";
        _trials.Account = Account(TrialState.Active);
        _client.DefaultRequestHeaders.Add("Cookie", "hts.trial-access=unit-owner-cookie");
        var token = await CsrfAsync(review);

        // The customer selected an upgrade before the expiry, but the server-side view changed
        // before POST. The request must stay an upgrade so the service rejects stale ownership.
        _trials.Account = Account(TrialState.Deleted);
        if (!keepCookie) _client.DefaultRequestHeaders.Remove("Cookie");
        using var response = await _client.PostAsync(review, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "checkout", ["PlanSlug"] = "zomboid-4gb", ["Period"] = "monthly",
            ["TrialUpgrade"] = "true", ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.IsType<CheckoutRequest>(_checkout.LastRequest);
        Assert.True(request.IsTrialUpgrade);
        Assert.Equal(keepCookie ? "unit-owner-cookie" : null, request.TrialAccessToken);
    }

    private sealed class RecordingTrialService : ITrialService
    {
        public bool IsAvailable { get; set; } = true;
        public IReadOnlyList<TrialGameProfile> AvailableProfiles => [];
        public List<TrialSelectionRequest> Requests { get; } = [];
        public int ConfirmCalls { get; private set; }
        public int RecoveryCalls { get; private set; }
        public TrialAccountView? Account { get; set; }

        public Task<TrialRequestResult> RequestAsync(TrialSelectionRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(new TrialRequestResult(TrialRequestOutcome.Sent)); }
        public Task<TrialRequestResult> RecoverAccessAsync(string email, string locale, CancellationToken cancellationToken = default)
        { RecoveryCalls++; return Task.FromResult(new TrialRequestResult(TrialRequestOutcome.Sent)); }
        public Task<TrialConfirmationResult> ConfirmAsync(string verificationToken, CancellationToken cancellationToken = default)
        { ConfirmCalls++; return Task.FromResult(new TrialConfirmationResult(TrialConfirmationOutcome.Confirmed, "unit-owner-token")); }
        public Task<TrialAccountView?> GetByAccessTokenAsync(string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);
        public Task<TrialUpgradeReservation> ReserveUpgradeAsync(Guid trialId, string accessToken, Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrialUpgradeReservation(false));
        public Task ReleaseUpgradeAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TrialConversionResult> ConvertPaidOrderAsync(Guid orderId, TrialResourceLimits limits, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrialConversionResult(TrialConversionOutcome.NotLinked));
        public Task ProcessDueAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingPanelPasswords : IPanelPasswordSetup
    {
        public static readonly Uri Link = new("https://auth.example.test/if/flow/default-recovery-flow/?flow_token=unit-link");
        public bool IsConfigured => true;
        public PanelPasswordSetupOutcome Outcome { get; set; } = PanelPasswordSetupOutcome.Ready;
        public List<(string Email, string DisplayName)> Requests { get; } = [];
        public Task<PanelPasswordSetupResult> CreateLinkAsync(string verifiedEmail, string displayName, CancellationToken cancellationToken)
        {
            Requests.Add((verifiedEmail, displayName));
            return Task.FromResult(new PanelPasswordSetupResult(Outcome, Outcome == PanelPasswordSetupOutcome.Ready ? Link : null));
        }
    }

    private sealed class RecordingCheckoutService : IStripeCheckoutService
    {
        public bool IsConfigured => true;
        public CheckoutRequest? LastRequest { get; private set; }
        public Task<CheckoutStart> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new CheckoutStart(CheckoutOutcome.Rejected, null, null));
        }
        public Task<Session?> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<Session?>(null);
        public Task HandleCompletedCheckoutAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleExpiredCheckoutAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleSubscriptionStateAsync(Subscription subscription, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleInvoiceStateAsync(Invoice invoice, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool ValidateOrderPrice(Order order, long? amountTotalMinor, string? currency) => false;
    }
}
