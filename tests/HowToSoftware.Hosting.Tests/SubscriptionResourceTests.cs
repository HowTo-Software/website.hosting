using System.Net;
using System.Text;
using System.Text.Json;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Subscriptions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

public sealed class SubscriptionResourceTests
{
    [Fact]
    public async Task PaidBuildUpdatePreservesIdentityAllocationAndOperatorSettingsAndIsRetrySafe()
    {
        using var h = new Harness();
        await h.Resources.ApplyAsync(h.Service, h.Change, default);
        await h.Resources.ApplyAsync(h.Service, h.Change, default);
        Assert.Equal(2, h.Writes.Count);
        foreach (var write in h.Writes)
        {
            Assert.Equal("/api/application/servers/42/build", write.Path);
            using var json = JsonDocument.Parse(write.Body);
            var root = json.RootElement;
            Assert.Equal(77, root.GetProperty("allocation").GetInt32());
            var limits = root.GetProperty("limits");
            Assert.Equal(8192, limits.GetProperty("memory").GetInt32());
            Assert.Equal(128, limits.GetProperty("swap").GetInt32());
            Assert.Equal(600, limits.GetProperty("io").GetInt32());
            Assert.Equal("0-1", limits.GetProperty("threads").GetString());
            Assert.True(root.GetProperty("oom_disabled").GetBoolean());
            Assert.False(root.TryGetProperty("environment", out _));
            Assert.False(root.TryGetProperty("startup", out _));
            Assert.False(root.TryGetProperty("egg", out _));
            Assert.False(root.TryGetProperty("name", out _));
        }
        Assert.Equal("same-server-uuid", h.Server.Uuid);
        Assert.Equal(15, h.Server.Egg);
        Assert.Equal(9, h.Server.Node);
    }
    [Theory]
    [InlineData("uuid")]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task MismatchedIdentityCannotMutateAnyServer(string mismatch)
    {
        using var h = new Harness();
        if (mismatch == "uuid") h.Server = h.Server with { Uuid = "other-server" };
        if (mismatch == "owner") h.Server = h.Server with { User = 99 };
        if (mismatch == "admin") h.User = h.User with { RootAdmin = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Resources.ApplyAsync(h.Service, h.Change, default));
        Assert.Empty(h.Writes);
    }
    [Theory]
    [InlineData("full")]
    [InlineData("unknown")]
    [InlineData("maintenance")]
    [InlineData("manual")]
    public async Task CapacityAndOperatorDriftFailClosedBeforeAnUpgrade(string reason)
    {
        using var h = new Harness();
        if (reason == "full") h.Node = h.Node with { MemoryMib = 8192, AllocatedResources = new() { MemoryMib = 8192, DiskMib = 25600 } };
        if (reason == "unknown") h.Node = h.Node with { AllocatedResources = null };
        if (reason == "maintenance") h.Node = h.Node with { MaintenanceMode = true };
        if (reason == "manual") h.Server = h.Server with { Limits = h.Server.Limits! with { Memory = 16384 } };
        Assert.False(await h.Resources.CanUpgradeAsync(h.Service, PlanChangeRules.Limits(h.Change), default));
        Assert.Empty(h.Writes);
    }
    [Fact]
    public async Task DiskAllocationIsNeverMistakenForActualUsedDiskAndReduced()
    {
        using var h = new Harness();
        h.Change.DiskMb = 12800; h.Change.Direction = PlanChangeDirection.Downgrade;
        await Assert.ThrowsAsync<UnsafeResourceReductionException>(() => h.Resources.ApplyAsync(h.Service, h.Change, default));
        Assert.Empty(h.Writes);
        Assert.Equal(25600, h.Server.Limits!.Disk);
    }
    private sealed class Harness : HttpMessageHandler
    {
        private readonly HttpClient _http;
        public ManagedService Service = new SubscriptionManagementTests.Harness().Managed;
        public SubscriptionPlanChange Change = new() { MemoryMb = 8192, CpuPercent = 300, DiskMb = 25600, BackupLimit = 1, DatabaseLimit = 0, AllocationLimit = 1, Direction = PlanChangeDirection.Upgrade };
        public PterodactylUser User = new() { Id = 7, Email = "owner@example.test" };
        public PterodactylServer Server = new() { Id = 42, Uuid = "same-server-uuid", User = 7, Egg = 15, Node = 9, Allocation = 77,
            Limits = new() { Memory = 4096, Disk = 25600, Cpu = 300, Swap = 128, Io = 600, Threads = "0-1", OomDisabled = true },
            FeatureLimits = new() { Allocations = 1, Backups = 1, Databases = 0 } };
        public PterodactylNode Node = new() { Id = 9, MemoryMib = 65536, DiskMib = 1048576,
            AllocatedResources = new() { MemoryMib = 4096, DiskMib = 25600 } };
        public List<(string Path, string Body)> Writes = [];
        public PterodactylSubscriptionResources Resources;
        public Harness()
        {
            _http = new(this, disposeHandler: false);
            var client = new PterodactylClient(_http, new Monitor<PterodactylOptions>(new()
                { ApiKey = "ptla_FAKE_TEST_KEY", BaseUrl = "https://panel.example.test" }), NullLogger<PterodactylClient>.Instance);
            Resources = new(client);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object response;
            if (request.Method == HttpMethod.Patch)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Writes.Add((request.RequestUri!.AbsolutePath, body));
                using var json = JsonDocument.Parse(body);
                var limits = json.RootElement.GetProperty("limits");
                Server = Server with { Limits = Server.Limits! with { Memory = limits.GetProperty("memory").GetInt32(), Cpu = limits.GetProperty("cpu").GetInt32(), Disk = limits.GetProperty("disk").GetInt32() } };
                response = new { attributes = Server };
            }
            else if (request.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)) response = new { data = new[] { new { attributes = User } } };
            else if (request.RequestUri.AbsolutePath.EndsWith("/nodes", StringComparison.Ordinal)) response = new { data = new[] { new { attributes = Node } } };
            else response = new { attributes = Server };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
