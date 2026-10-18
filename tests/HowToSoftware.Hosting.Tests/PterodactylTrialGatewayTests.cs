using System.Net;
using System.Text;
using System.Text.Json;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl.Models;
using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Services;
using HowToSoftware.Hosting.Services.Provisioning;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

public sealed class PterodactylTrialGatewayTests
{
    private static readonly Guid TrialId = Guid.Parse("c27ab933-9aef-4767-bd29-e6c794ff95ab");
    private static string TrialExternalId => $"hts-trial-{TrialId:N}";

    [Fact]
    public async Task EmailLookupEscapesFilterAndRejectsPartialMatchesAcrossPages()
    {
        using var h = new Harness();
        h.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { new { attributes = new PterodactylUser { Id = 1, Email = "prefix.alice+trial@example.test" } } },
            meta = new { pagination = new { total_pages = 2, current_page = 1 } }
        });
        h.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { new { attributes = new PterodactylUser { Id = 7, Email = "Alice+Trial@Example.Test" } } },
            meta = new { pagination = new { total_pages = 2, current_page = 2 } }
        });
        var user = await h.Client.FindUserByEmailAsync("alice+trial@example.test");
        Assert.Equal(7, user!.Id);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("filter%5Bemail%5D=alice%2Btrial%40example.test", h.Requests[0].Path);
        Assert.Contains("page=2", h.Requests[1].Path);
        Assert.DoesNotContain(h.Log.Messages, message => message.Contains("alice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PartialEmailMatchIsNeverReturnedAsTheRequestedAccount()
    {
        using var h = new Harness();
        h.EnqueueList(new PterodactylUser { Id = 9, Email = "attacker.alice@example.test" });
        Assert.Null(await h.Client.FindUserByEmailAsync("alice@example.test"));
    }

    [Fact]
    public async Task VerifiedExistingPanelAccountIsReusedWithoutChangingPasswordOrExternalId()
    {
        using var h = new Harness();
        h.EnqueueList(new PterodactylUser { Id = 7, Email = "alice@example.test", ExternalId = "manual-account" });
        var user = await h.Gateway.ResolveUserAsync("ALICE@example.test", "Alice", default);
        Assert.Equal(7, user.Id);
        Assert.Single(h.Requests);
        Assert.Equal("GET", h.Requests[0].Method);
    }

    [Fact]
    public async Task AdministrativeAccountIsNeverAdoptedForATrial()
    {
        using var h = new Harness();
        h.EnqueueList(new PterodactylUser { Id = 7, Email = "alice@example.test", RootAdmin = true });
        var error = await Assert.ThrowsAsync<PterodactylApiException>(() =>
            h.Gateway.ResolveUserAsync("alice@example.test", "Alice", default));
        Assert.Equal(PterodactylFailure.Forbidden, error.Failure);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task NewPanelAccountUsesPanelPasswordSetupInsteadOfExposingAPassword()
    {
        using var h = new Harness();
        h.EnqueueList<PterodactylUser>();
        h.EnqueueItem(new PterodactylUser { Id = 7, Email = "alice@example.test" });
        var user = await h.Gateway.ResolveUserAsync("alice@example.test", "Alice Example", default);
        Assert.Equal(7, user.Id);
        using var payload = JsonDocument.Parse(h.Requests[1].Body!);
        Assert.False(payload.RootElement.TryGetProperty("password", out _));
        Assert.False(payload.RootElement.GetProperty("root_admin").GetBoolean());
        Assert.Equal("Alice", payload.RootElement.GetProperty("first_name").GetString());
        Assert.StartsWith("hts-trial-user:", payload.RootElement.GetProperty("external_id").GetString());
    }

    [Fact]
    public async Task ConcurrentPanelSignupRecoversOnlyTheExactAccount()
    {
        using var h = new Harness();
        h.EnqueueList<PterodactylUser>();
        h.Enqueue(HttpStatusCode.UnprocessableContent, new
        {
            errors = new[] { new { code = "ValidationException", status = "422", detail = "Email already exists.", meta = new { source_field = "email" } } }
        });
        h.EnqueueList(new PterodactylUser { Id = 7, Email = "alice@example.test" });
        Assert.Equal(7, (await h.Gateway.ResolveUserAsync("alice@example.test", "Alice", default)).Id);
        Assert.Equal(new[] { "GET", "POST", "GET" }, h.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task UnrelatedSignupValidationErrorIsNotTreatedAsAnExistingAccount()
    {
        using var h = new Harness();
        h.EnqueueList<PterodactylUser>();
        h.Enqueue(HttpStatusCode.UnprocessableContent, new { errors = new[] { new { code = "ValidationException", status = "422", detail = "Invalid username." } } });
        h.EnqueueList<PterodactylUser>();
        var error = await Assert.ThrowsAsync<PterodactylApiException>(() =>
            h.Gateway.ResolveUserAsync("alice@example.test", "Alice", default));
        Assert.Equal(PterodactylFailure.ValidationFailed, error.Failure);
    }

    [Fact]
    public async Task TrialCreationUsesConfiguredResourcesEggDefaultsAndPanelNodeSelection()
    {
        using var h = new Harness();
        h.Enqueue(HttpStatusCode.NotFound);
        h.EnqueueItem(new PterodactylEgg { Id = 13, Nest = 2, DockerImage = "approved-image", Startup = "approved-startup",
            Relationships = new() { Variables = new() { Data = [new() { Attributes = new() { EnvVariable = "REQUIRED", DefaultValue = "egg-value" } }] } } });
        h.EnqueueItem(Server());
        var result = await h.Gateway.CreateServerAsync(new(TrialId, TrialExternalId, 7, new(6144, 0, 25600), DisplayName: "Malaio"), default);
        Assert.Equal(42, result.Id);
        using var payload = JsonDocument.Parse(h.Requests[2].Body!);
        var root = payload.RootElement;
        Assert.Equal("24 Teste Malaio", root.GetProperty("name").GetString());
        var adminPassword = root.GetProperty("environment").GetProperty("ADMIN_PASSWORD").GetString();
        Assert.Equal(32, adminPassword!.Length);
        Assert.DoesNotContain(h.Log.Messages, line => line.Contains(adminPassword, StringComparison.Ordinal));
        Assert.Equal(6144, root.GetProperty("limits").GetProperty("memory").GetInt32());
        Assert.Equal(0, root.GetProperty("limits").GetProperty("cpu").GetInt32());
        Assert.Equal(25600, root.GetProperty("limits").GetProperty("disk").GetInt32());
        Assert.Equal(2, root.GetProperty("feature_limits").GetProperty("allocations").GetInt32());
        Assert.Equal("egg-value", root.GetProperty("environment").GetProperty("REQUIRED").GetString());
        Assert.Equal(3, root.GetProperty("deploy").GetProperty("locations")[0].GetInt32());
        Assert.False(root.TryGetProperty("allocation", out _));
        Assert.True(root.GetProperty("start_on_completion").GetBoolean());
    }

    [Fact]
    public async Task RetriedTrialCreationReturnsTheSameOwnedServerWithoutCreatingAnother()
    {
        using var h = new Harness();
        h.EnqueueItem(Server());
        var result = await h.Gateway.CreateServerAsync(new(TrialId, TrialExternalId, 7, new(6144, 0, 25600)), default);
        Assert.Equal(42, result.Id);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task SameExternalIdWithDifferentOwnerIsRejectedBeforeMutation()
    {
        using var h = new Harness();
        h.EnqueueItem(Server() with { User = 99 });
        var error = await Assert.ThrowsAsync<PterodactylApiException>(() =>
            h.Gateway.CreateServerAsync(new(TrialId, TrialExternalId, 7, new(6144, 0, 25600)), default));
        Assert.Equal(PterodactylFailure.Forbidden, error.Failure);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task ApprovedMinecraftProfileAndVersionOverrideOnlyTheirConfiguredVariable()
    {
        using var h = new Harness();
        h.Options.Profiles.Add(new TrialGameProfile
        {
            Id = "java-fabric", GameSlug = "minecraft", Edition = "java", Variant = "fabric", Enabled = true,
            NestId = 8, EggId = 91, LocationId = 9, DockerImage = "approved-java21",
            AllowedVersions = ["1.21.1"], VersionEnvironmentVariable = "MC_VERSION",
            Environment = new() { ["LOADER"] = "fabric" }, PortRange = ["25565-25575"]
        });
        h.Enqueue(HttpStatusCode.NotFound);
        h.EnqueueItem(new PterodactylEgg { Id = 91, Nest = 8, DockerImage = "egg-image", Startup = "java startup" });
        h.EnqueueItem(Server() with { Egg = 91 });
        var forged = new TrialGameProfile { DockerImage = "unapproved-image", Environment = new() { ["INJECTED"] = "bad" } };
        await h.Gateway.CreateServerAsync(new(TrialId, TrialExternalId, 7, new(6144, 0, 25600),
            "minecraft", "java-fabric", "1.21.1", forged), default);
        using var payload = JsonDocument.Parse(h.Requests[2].Body!);
        var root = payload.RootElement;
        Assert.Equal("approved-java21", root.GetProperty("docker_image").GetString());
        Assert.Equal("1.21.1", root.GetProperty("environment").GetProperty("MC_VERSION").GetString());
        Assert.Equal("fabric", root.GetProperty("environment").GetProperty("LOADER").GetString());
        Assert.False(root.GetProperty("environment").TryGetProperty("INJECTED", out _));
        Assert.Equal("25565-25575", root.GetProperty("deploy").GetProperty("port_range")[0].GetString());
    }

    [Fact]
    public async Task UnapprovedMinecraftVersionCannotProduceADeploymentRequest()
    {
        using var h = new Harness();
        h.Options.Profiles.Add(new TrialGameProfile
        {
            Id = "java", GameSlug = "minecraft", Enabled = true, NestId = 8, EggId = 91, LocationId = 9,
            AllowedVersions = ["1.21.1"], VersionEnvironmentVariable = "MC_VERSION"
        });
        h.Enqueue(HttpStatusCode.NotFound);
        var error = await Assert.ThrowsAsync<PterodactylApiException>(() =>
            h.Gateway.CreateServerAsync(new(TrialId, TrialExternalId, 7, new(6144, 0, 25600),
                "minecraft", "java", "curl malicious-url"), default));
        Assert.Equal(PterodactylFailure.ValidationFailed, error.Failure);
        Assert.Single(h.Requests);
        Assert.False(h.Gateway.CanProvision("minecraft", "java", "curl malicious-url"));
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("resume")]
    [InlineData("delete")]
    [InlineData("upgrade")]
    public async Task TrialOperationsCannotModifyAnUnrelatedPaidServer(string operation)
    {
        using var h = new Harness();
        h.EnqueueItem(Server() with { ExternalId = "hts-server:paid-order" });
        var error = await Assert.ThrowsAsync<PterodactylApiException>(() => operation switch
        {
            "suspend" => h.Gateway.SuspendAsync(42, default),
            "resume" => h.Gateway.ResumeAsync(42, default),
            "delete" => h.Gateway.DeleteAsync(42, default),
            _ => h.Gateway.UpgradeAsync(42, new(8192, 500, 40960), default)
        });
        Assert.Equal(PterodactylFailure.Forbidden, error.Failure);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task ExpirySuspendsAndResumeUnsuspendsWithoutReinstallingOrDeletingFiles()
    {
        using var h = new Harness();
        h.EnqueueItem(Server());
        h.Enqueue(HttpStatusCode.NoContent);
        h.EnqueueItem(Server() with { Status = "suspended", Suspended = true });
        h.Enqueue(HttpStatusCode.NoContent);
        await h.Gateway.SuspendAsync(42, default);
        await h.Gateway.ResumeAsync(42, default);
        Assert.Equal(new[] { "GET", "POST", "GET", "POST" }, h.Requests.Select(r => r.Method));
        Assert.EndsWith("/servers/42/suspend", h.Requests[1].Path);
        Assert.EndsWith("/servers/42/unsuspend", h.Requests[3].Path);
        Assert.DoesNotContain(h.Requests, r => r.Path.Contains("reinstall", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AlreadyDeletedTrialCleanupIsIdempotent()
    {
        using var h = new Harness();
        h.Enqueue(HttpStatusCode.NotFound);
        await h.Gateway.DeleteAsync(42, default);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task RemovingNewSignupTemplatesDoesNotPreventExistingTrialExpiry()
    {
        using var h = new Harness();
        h.PanelSettings.EggId = 0;
        h.PanelSettings.NestId = 0;
        h.PanelSettings.LocationId = 0;
        Assert.True(h.Gateway.IsConfigured);
        Assert.False(h.Gateway.CanProvision("project-zomboid"));
        h.EnqueueItem(Server());
        h.Enqueue(HttpStatusCode.NoContent);
        await h.Gateway.SuspendAsync(42, default);
        Assert.EndsWith("/servers/42/suspend", h.Requests[1].Path);
    }

    [Fact]
    public async Task PaidUpgradeKeepsServerIdentityFilesAndBoundPortsWhileApplyingPlanResources()
    {
        using var h = new Harness();
        var original = Server() with
        {
            Limits = new() { Memory = 6144, Cpu = 0, Disk = 25600, Swap = -1, Io = 750, Threads = "0-3", OomDisabled = true },
            FeatureLimits = new() { Backups = 1, Databases = 0, Allocations = 5 },
            Relationships = new() { Allocations = new() { Data = [new() { Attributes = new() { Id = 24 } }, new() { Attributes = new() { Id = 25 } }] } }
        };
        h.EnqueueItem(original);
        h.EnqueueItem(original with { Limits = original.Limits with { Memory = 16384, Cpu = 900, Disk = 40960 } });
        await h.Gateway.UpgradeAsync(42, new(16384, 900, 40960, Backups: 10, Databases: 3, Allocations: 1), default);
        Assert.Equal(new[] { "GET", "PATCH" }, h.Requests.Select(r => r.Method));
        Assert.EndsWith("/servers/42/build", h.Requests[1].Path);
        using var payload = JsonDocument.Parse(h.Requests[1].Body!);
        var root = payload.RootElement;
        Assert.Equal(24, root.GetProperty("allocation").GetInt32());
        var limits = root.GetProperty("limits");
        Assert.Equal(16384, limits.GetProperty("memory").GetInt32());
        Assert.Equal(900, limits.GetProperty("cpu").GetInt32());
        Assert.Equal(40960, limits.GetProperty("disk").GetInt32());
        Assert.Equal(-1, limits.GetProperty("swap").GetInt32());
        Assert.Equal(750, limits.GetProperty("io").GetInt32());
        Assert.Equal("0-3", limits.GetProperty("threads").GetString());
        Assert.True(root.GetProperty("oom_disabled").GetBoolean());
        Assert.Equal(2, root.GetProperty("feature_limits").GetProperty("allocations").GetInt32());
        Assert.Equal(10, root.GetProperty("feature_limits").GetProperty("backups").GetInt32());
        Assert.Equal(3, root.GetProperty("feature_limits").GetProperty("databases").GetInt32());
        Assert.False(root.TryGetProperty("egg", out _));
        Assert.False(root.TryGetProperty("startup", out _));
        Assert.False(root.TryGetProperty("external_id", out _));
        Assert.False(root.TryGetProperty("remove_allocations", out _));
    }

    [Fact]
    public async Task InspectRecognizesInstallingSuspendedAndFailedInstallationStates()
    {
        using var h = new Harness();
        foreach (var state in new[] { "installing", "install_failed", "suspended" })
        {
            h.EnqueueItem(Server() with { Status = state });
            var result = await h.Gateway.InspectServerAsync(42, default);
            Assert.Equal(state == "suspended", result!.Installed);
            Assert.Equal(state == "suspended", result.Suspended);
        }
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("unsuspend")]
    public async Task ServerActionsRequireARealNoContentSuccess(string action)
    {
        using var h = new Harness();
        h.Enqueue(HttpStatusCode.OK, new { message = "not a valid action acknowledgement" });
        var error = await Assert.ThrowsAsync<PterodactylApiException>(() => action == "suspend"
            ? h.Client.SuspendServerAsync(42) : h.Client.UnsuspendServerAsync(42));
        Assert.Equal(PterodactylFailure.PanelError, error.Failure);
    }

    [Fact]
    public async Task PaidProvisioningReusesManualCustomerAccountWithoutDuplicateUserCreation()
    {
        using var h = new Harness();
        h.Enqueue(HttpStatusCode.NotFound); // Server external id.
        h.EnqueueItem(new PterodactylEgg { Id = 13, DockerImage = "image", Startup = "startup" });
        h.Enqueue(HttpStatusCode.NotFound); // HTS user external id.
        h.EnqueueList(new PterodactylUser { Id = 7, Email = "alice@example.test", ExternalId = "legacy-user" });
        h.EnqueueItem(Server() with { ExternalId = PterodactylNaming.ServerExternalId(TrialId) });
        var result = await h.Provisioner.ProvisionAsync(PaidRequest());
        Assert.Equal(ProvisioningOutcome.Created, result.Outcome);
        Assert.True(result.UserWasReused);
        Assert.Equal(7, result.PanelUserId);
        Assert.DoesNotContain(h.Requests, r => r.Method == "POST" && r.Path.EndsWith("/users", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PaidProvisioningRecoversUserUniquenessRaceBeforeCreatingTheServer()
    {
        using var h = new Harness();
        h.Enqueue(HttpStatusCode.NotFound);
        h.EnqueueItem(new PterodactylEgg { Id = 13, DockerImage = "image", Startup = "startup" });
        h.Enqueue(HttpStatusCode.NotFound);
        h.EnqueueList<PterodactylUser>();
        h.Enqueue(HttpStatusCode.UnprocessableContent, new { errors = new[] { new { code = "ValidationException", status = "422", detail = "Email already exists.", meta = new { source_field = "email" } } } });
        h.Enqueue(HttpStatusCode.NotFound);
        h.EnqueueList(new PterodactylUser { Id = 7, Email = "alice@example.test" });
        h.EnqueueItem(Server() with { ExternalId = PterodactylNaming.ServerExternalId(TrialId) });
        var result = await h.Provisioner.ProvisionAsync(PaidRequest());
        Assert.Equal(ProvisioningOutcome.Created, result.Outcome);
        Assert.True(result.UserWasReused);
        Assert.Equal(7, result.PanelUserId);
    }

    [Fact]
    public async Task PaidProvisioningRefusesAdministrativeCustomerAccount()
    {
        using var h = new Harness();
        h.Enqueue(HttpStatusCode.NotFound);
        h.EnqueueItem(new PterodactylEgg { Id = 13, DockerImage = "image", Startup = "startup" });
        h.Enqueue(HttpStatusCode.NotFound);
        h.EnqueueList(new PterodactylUser { Id = 7, Email = "alice@example.test", RootAdmin = true });
        var result = await h.Provisioner.ProvisionAsync(PaidRequest());
        Assert.Equal(ProvisioningOutcome.Failed, result.Outcome);
        Assert.Equal(PterodactylFailure.Forbidden, result.Failure);
        Assert.DoesNotContain(h.Requests, r => r.Method == "POST");
    }

    private static ProvisioningRequest PaidRequest() => new()
    {
        RequestId = TrialId, CustomerId = Guid.NewGuid(), CustomerEmail = "alice@example.test",
        PlanSlug = "zomboid-4gb", ServerName = "Community"
    };

    private static PterodactylServer Server() => new()
    {
        Id = 42, ExternalId = TrialExternalId, Identifier = "trial123", Uuid = "panel-uuid",
        User = 7, Node = 4, Allocation = 24, Egg = 13,
        Limits = new() { Memory = 6144, Cpu = 0, Disk = 25600 },
        FeatureLimits = new() { Backups = 1, Databases = 0, Allocations = 2 }
    };

    private sealed record Request(string Method, string Path, string? Body);

    private sealed class Harness : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        private readonly HttpClient _http;
        public List<Request> Requests { get; } = [];
        public TrialOptions Options { get; } = new();
        public PterodactylOptions PanelSettings { get; } = new()
        {
            BaseUrl = "https://panel.example.test", ApiKey = "ptla_FAKE_TEST_KEY",
            NestId = 2, EggId = 13, LocationId = 3
        };
        public LogSink<PterodactylClient> Log { get; } = new();
        public PterodactylClient Client { get; }
        public TrialPanelGateway Gateway { get; }
        public ProvisioningService Provisioner { get; }

        public Harness()
        {
            _http = new(this, disposeHandler: false);
            var panelOptions = new Monitor<PterodactylOptions>(PanelSettings);
            Client = new(_http, panelOptions, Log);
            var templates = new GameTemplateCatalog(panelOptions);
            Gateway = new(Client, templates, new Monitor<TrialOptions>(Options));
            var plans = new StaticPlanCatalogService(TestLocalizer.For<HomeText>(), Microsoft.Extensions.Options.Options.Create(new HostingPlanPricingOptions()));
            Provisioner = new(Client, plans, templates, panelOptions, NullLogger<ProvisioningService>.Instance);
        }

        public void Enqueue(HttpStatusCode status, object? body = null) => _responses.Enqueue(new(status)
        {
            Content = new StringContent(body is null ? string.Empty : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        });
        public void EnqueueItem<T>(T item) => Enqueue(HttpStatusCode.OK, new { attributes = item });
        public void EnqueueList<T>(params T[] items) => Enqueue(HttpStatusCode.OK, new
        {
            data = items.Select(item => new { attributes = item }).ToArray()
        });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Count > 0 ? _responses.Dequeue() : throw new InvalidOperationException("Unexpected panel request.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _http.Dispose();
                foreach (var response in _responses) response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class LogSink<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
