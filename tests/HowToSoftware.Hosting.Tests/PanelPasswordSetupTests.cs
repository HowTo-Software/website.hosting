using System.Net;
using System.Text;
using System.Text.Json;
using HowToSoftware.Hosting.Services.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

/// <summary>Customer directory boundaries against fake Authentik responses; no identity provider is contacted.</summary>
public sealed class PanelPasswordSetupTests
{
    private static AuthentikOptions Settings() => new() { BaseUrl = "https://auth.example.test", ApiToken = "unit-test-only" };

    private static AuthentikPanelPasswordSetup Service(FakeDirectory directory) =>
        new(directory, new Monitor(Settings()), NullLogger<AuthentikPanelPasswordSetup>.Instance);

    [Fact]
    public async Task AFirstTimeCustomerBecomesAnExternalCustomerGroupMemberAndGetsAOneTimeLink()
    {
        var directory = new FakeDirectory();
        var result = await Service(directory).CreateLinkAsync("Player@Example.test", "Player One", default);
        Assert.Equal(PanelPasswordSetupOutcome.Ready, result.Outcome);
        Assert.Equal(FakeDirectory.Link, result.Link);
        Assert.Equal(new AuthentikNewCustomer("player@example.test", "Player One", "player@example.test", "customers/hts", "group-uuid"),
            Assert.Single(directory.Created));
        Assert.Equal(new[] { directory.Users.Single().Pk }, directory.LinkedUsers);
    }

    [Fact]
    public async Task AReturningCustomerReusesItsAccount()
    {
        var directory = new FakeDirectory();
        directory.Users.Add(new(7, "player@example.test", "player@example.test", true, false, "external", ["hts-customers"]));
        var result = await Service(directory).CreateLinkAsync("player@example.test", "Player", default);
        Assert.Equal(PanelPasswordSetupOutcome.Ready, result.Outcome);
        Assert.Empty(directory.Created);
        Assert.Equal(new[] { 7 }, directory.LinkedUsers);
    }

    [Theory]
    [InlineData("internal", false, "hts-customers")]
    [InlineData("external", true, "hts-customers")]
    [InlineData("external", false, "staff")]
    public async Task StaffAdministratorAndDirectoryAccountsAreNeverReset(string type, bool superuser, string group)
    {
        var directory = new FakeDirectory();
        directory.Users.Add(new(3, "jdoe", "Player@Example.test", true, superuser, type, [group]));
        var result = await Service(directory).CreateLinkAsync("player@example.test", "Player", default);
        Assert.Equal(PanelPasswordSetupOutcome.DirectoryAccount, result.Outcome);
        Assert.Null(result.Link);
        Assert.Empty(directory.Created);
        Assert.Empty(directory.LinkedUsers);
    }

    [Fact]
    public async Task ADisabledCustomerStaysDisabledAndGetsNoLink()
    {
        var directory = new FakeDirectory();
        directory.Users.Add(new(7, "player@example.test", "player@example.test", false, false, "external", ["hts-customers"]));
        Assert.Equal(PanelPasswordSetupOutcome.Unavailable,
            (await Service(directory).CreateLinkAsync("player@example.test", "Player", default)).Outcome);
        Assert.Empty(directory.LinkedUsers);
    }

    [Fact]
    public async Task ANewAccountThatAuthentikReportsAsSuperuserGetsNoLink()
    {
        // For example, a customer group that was mistakenly given superuser rights.
        var directory = new FakeDirectory { CreateAsSuperuser = true };
        Assert.Equal(PanelPasswordSetupOutcome.Unavailable,
            (await Service(directory).CreateLinkAsync("player@example.test", "Player", default)).Outcome);
        Assert.Single(directory.Created);
        Assert.Empty(directory.LinkedUsers);
    }

    [Fact]
    public async Task AnAuthentikOutageIsReportedAsUnavailableInsteadOfThrowing()
    {
        var directory = new FakeDirectory { FailLookups = true };
        Assert.Equal(PanelPasswordSetupOutcome.Unavailable,
            (await Service(directory).CreateLinkAsync("player@example.test", "Player", default)).Outcome);
    }

    [Fact]
    public async Task RecoveryLinksFallBackToGetOnOlderAuthentikAndMustStayOnTheConfiguredOrigin()
    {
        var older = new StubHandler(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : Json("""{"link":"https://auth.example.test/if/flow/default-recovery-flow/?flow_token=unit"}"""));
        using var olderHttp = new HttpClient(older);
        var link = await Client(olderHttp).CreateRecoveryLinkAsync(7, default);
        Assert.Equal("https://auth.example.test/if/flow/default-recovery-flow/?flow_token=unit", link.AbsoluteUri);
        Assert.Equal(new[] { "POST /api/v3/core/users/7/recovery/", "GET /api/v3/core/users/7/recovery/" },
            older.Requests.Select(x => $"{x.Method} {x.Path}"));
        Assert.All(older.Requests, x => Assert.Equal("Bearer unit-test-only", x.Authorization));

        using var foreignHttp = new HttpClient(new StubHandler(_ => Json("""{"link":"https://phishing.example/if/flow/x/"}""")));
        await Assert.ThrowsAsync<AuthentikApiException>(() => Client(foreignHttp).CreateRecoveryLinkAsync(7, default));
    }

    [Fact]
    public async Task CustomersAreCreatedAsExternalGroupMembersWithoutAnyPassword()
    {
        var handler = new StubHandler(_ => Json("""
            {"pk":42,"username":"player@example.test","email":"player@example.test","is_active":true,
             "is_superuser":false,"type":"external","groups_obj":[{"pk":"group-uuid","name":"hts-customers"}]}
            """, HttpStatusCode.Created));
        using var http = new HttpClient(handler);
        var user = await Client(http).CreateCustomerAsync(
            new("player@example.test", "Player", "player@example.test", "customers/hts", "group-uuid"), default);

        Assert.Equal((42, "player@example.test", true, false, "external"),
            (user.Pk, user.Email, user.IsActive, user.IsSuperuser, user.Type));
        Assert.Equal(new[] { "hts-customers" }, user.Groups);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST /api/v3/core/users/", $"{request.Method} {request.Path}");
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("external", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("customers/hts", body.RootElement.GetProperty("path").GetString());
        Assert.Equal("group-uuid", body.RootElement.GetProperty("groups")[0].GetString());
        Assert.True(body.RootElement.GetProperty("is_active").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task EmailLookupReadsEverySearchPageAndKeepsOnlyExactAddresses()
    {
        var handler = new StubHandler(request => Json(request.RequestUri!.Query.Contains("page=1&", StringComparison.Ordinal)
            ? """
              {"pagination":{"next":2},"results":[
                {"pk":2,"username":"p2","email":"otherplayer@example.test","is_active":true,"is_superuser":false,"type":"external",
                 "groups_obj":[]}
              ]}
              """
            : """
              {"pagination":{"next":0},"results":[
                {"pk":1,"username":"p1","email":"Player@Example.test","is_active":true,"is_superuser":false,"type":"external",
                 "groups_obj":[{"pk":"g","name":"hts-customers"}]}
              ]}
              """));
        using var http = new HttpClient(handler);
        var users = await Client(http).FindUsersByEmailAsync("player@example.test", default);
        Assert.Equal(1, Assert.Single(users).Pk);
        Assert.Equal(new[]
        {
            "/api/v3/core/users/?search=player%40example.test&page=1&page_size=100",
            "/api/v3/core/users/?search=player%40example.test&page=2&page_size=100"
        }, handler.Requests.Select(x => x.Path));
    }

    [Fact]
    public async Task ALookupThatCannotBeCompletedFailsClosed()
    {
        var handler = new StubHandler(_ => Json("""{"pagination":{"next":99},"results":[]}"""));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<AuthentikApiException>(() => Client(http).FindUsersByEmailAsync("player@example.test", default));
        Assert.Equal(10, handler.Requests.Count);
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("https://auth.example.test", "unit-secret-token", true)]
    [InlineData("http://auth.example.test", "unit-secret-token", false)]
    [InlineData("https://auth.example.test/application", "unit-secret-token", false)]
    [InlineData("https://auth.example.test", "", false)]
    [InlineData("", "unit-secret-token", false)]
    public void TheDirectoryMustBeAnHttpsOriginWithAToken(string baseUrl, string token, bool valid)
    {
        var result = new AuthentikOptionsValidator().Validate(null, new AuthentikOptions { BaseUrl = baseUrl, ApiToken = token });
        Assert.Equal(valid, result.Succeeded);
        Assert.DoesNotContain("unit-secret-token", string.Join(" ", result.Failures ?? []), StringComparison.Ordinal);
    }

    private static AuthentikClient Client(HttpClient http) => new(http, new Monitor(Settings()));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Monitor(AuthentikOptions value) : IOptionsMonitor<AuthentikOptions>
    {
        public AuthentikOptions CurrentValue => value;
        public AuthentikOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AuthentikOptions, string?> listener) => null;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.ToString()));
            return respond(request);
        }
    }

    private sealed class FakeDirectory : IAuthentikClient
    {
        public static readonly Uri Link = new("https://auth.example.test/if/flow/default-recovery-flow/?flow_token=unit");
        public bool IsConfigured => true;
        public bool FailLookups { get; init; }
        public bool CreateAsSuperuser { get; init; }
        public List<AuthentikUser> Users { get; } = [];
        public List<AuthentikNewCustomer> Created { get; } = [];
        public List<int> LinkedUsers { get; } = [];

        public Task<IReadOnlyList<AuthentikUser>> FindUsersByEmailAsync(string email, CancellationToken cancellationToken) =>
            FailLookups
                ? throw new AuthentikApiException(HttpStatusCode.ServiceUnavailable, "Authentik rejected the request with HTTP 503.")
                : Task.FromResult<IReadOnlyList<AuthentikUser>>(Users
                    .Where(user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)).ToArray());

        public Task<string?> FindGroupIdAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(name == "hts-customers" ? "group-uuid" : null);

        public Task<AuthentikUser> CreateCustomerAsync(AuthentikNewCustomer customer, CancellationToken cancellationToken)
        {
            Created.Add(customer);
            var user = new AuthentikUser(100 + Users.Count, customer.Username, customer.Email, true, CreateAsSuperuser, "external",
                ["hts-customers"]);
            Users.Add(user);
            return Task.FromResult(user);
        }

        public Task<Uri> CreateRecoveryLinkAsync(int userPk, CancellationToken cancellationToken)
        {
            LinkedUsers.Add(userPk);
            return Task.FromResult(Link);
        }
    }
}
