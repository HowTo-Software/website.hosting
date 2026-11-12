using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HowToSoftware.Hosting.Tests;

/// <summary>Lifecycle boundary tests use a deterministic clock and faulting panel/email adapters; no remote service is contacted.</summary>
public sealed class ServerTrialLifecycleTests
{
    [Fact]
    public async Task VerificationIsRequiredAndInstallationStartsTheClock()
    {
        var f = new Fixture();
        Assert.Equal(TrialRequestOutcome.Sent, (await f.Service.RequestAsync(new("Player@Example.com", "Player"))).Outcome);
        await f.Service.ProcessDueAsync();
        Assert.Equal(0, f.Panel.CreateCalls);
        Assert.Null(f.Store.Single.StartedAt);
        var access = await f.ConfirmAsync();
        await f.Service.ProcessDueAsync();
        Assert.Equal(TrialState.Installing, f.Store.Single.State);
        Assert.Null(f.Store.Single.StartedAt);
        f.Clock.Advance(TimeSpan.FromHours(3));
        f.Panel.Installed = true;
        await f.Service.ProcessDueAsync();
        Assert.Equal(f.Clock.GetUtcNow(), f.Store.Single.StartedAt);
        Assert.Equal(f.Clock.GetUtcNow().AddHours(24), f.Store.Single.ExpiresAt);
        Assert.Equal(6144, f.Panel.LastRequest!.Limits.MemoryMb);
        Assert.Equal(0, f.Panel.LastRequest.Limits.CpuPercent);
        Assert.Equal(25600, f.Panel.LastRequest.Limits.DiskMb);
        Assert.Equal(TrialState.Active, (await f.Service.GetByAccessTokenAsync(access))!.State);
        Assert.Equal(1, f.Mail.ReadyCalls);
    }

    [Fact]
    public async Task ExactExpirySuspendsAndSeventyTwoHoursLaterDeletesOnlyTheServer()
    {
        var f = new Fixture();
        await f.ActivateAsync();
        var serverId = f.Store.Single.PterodactylServerId;
        f.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        await f.Service.ProcessDueAsync();
        Assert.Equal(0, f.Panel.SuspendCalls);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.Service.ProcessDueAsync();
        Assert.Equal(TrialState.Expired, f.Store.Single.State);
        Assert.Equal(1, f.Panel.SuspendCalls);
        Assert.Equal(f.Store.Single.ExpiresAt!.Value.AddHours(72), f.Store.Single.DeleteAfter);
        Assert.Equal(1, f.Mail.ExpiredCalls);
        f.Clock.Advance(TimeSpan.FromHours(72) - TimeSpan.FromSeconds(1));
        await f.Service.ProcessDueAsync();
        Assert.Equal(0, f.Panel.DeleteCalls);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.Service.ProcessDueAsync();
        Assert.Equal(TrialState.Deleted, f.Store.Single.State);
        Assert.Equal(serverId, f.Panel.LastDeletedId);
        Assert.Single(f.Store.Rows); // Permanent email/account entitlement survives cleanup.
        Assert.Equal(1, f.Mail.DeletedCalls);
    }

    [Fact]
    public async Task RepeatedOrCrossGameRequestsRecoverTheExistingClaimWithoutRestartingIt()
    {
        var f = new Fixture();
        var oldAccess = await f.ActivateAsync();
        var original = f.Store.Single;
        f.Clock.Advance(TimeSpan.FromHours(2));
        await f.Service.RequestAsync(new("PLAYER@example.com", "Changed", "minecraft", "fake-profile", "bad-version"));
        var newAccess = await f.ConfirmAsync();
        await f.Service.ProcessDueAsync();
        Assert.Single(f.Store.Rows);
        Assert.Equal("project-zomboid", f.Store.Single.GameSlug);
        Assert.Equal(original.Id, f.Store.Single.Id);
        Assert.Equal(original.StartedAt, f.Store.Single.StartedAt);
        Assert.Equal(original.ExpiresAt, f.Store.Single.ExpiresAt);
        Assert.Equal(1, f.Panel.CreateCalls);
        Assert.Null(await f.Service.GetByAccessTokenAsync(oldAccess));
        Assert.NotNull(await f.Service.GetByAccessTokenAsync(newAccess));
    }

    [Fact]
    public async Task RecoveryStillWorksWhenNewTrialsAndProfilesAreDisabled()
    {
        var f = new Fixture();
        await f.ActivateAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        f.Options.Enabled = false;
        f.Panel.Configured = false;
        Assert.Equal(TrialRequestOutcome.Sent, (await f.Service.RecoverAccessAsync("player@example.com", "pt-BR")).Outcome);
        var access = await f.ConfirmAsync();
        Assert.Equal("pt-BR", f.Mail.LastLocale);
        Assert.Equal(TrialState.Active, (await f.Service.GetByAccessTokenAsync(access))!.State);
        Assert.Single(f.Store.Rows);
    }

    [Fact]
    public async Task RecoveryOfUnknownEmailNeverCreatesAnEntitlement()
    {
        var f = new Fixture();
        Assert.Equal(TrialRequestOutcome.Sent, (await f.Service.RecoverAccessAsync("unknown@example.com", "en")).Outcome);
        Assert.Empty(f.Store.Rows);
        Assert.Null(f.Mail.LastVerificationUrl);
    }

    [Fact]
    public async Task RecoveryResendsTheConfirmationOfARequestThatWasNeverConfirmed()
    {
        var f = new Fixture();
        await f.Service.RequestAsync(new("player@example.com", "Player"));
        var lostLink = f.Mail.LastVerificationUrl;
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(TrialRequestOutcome.Sent, (await f.Service.RecoverAccessAsync("player@example.com", "pt-BR")).Outcome);
        Assert.NotEqual(lostLink, f.Mail.LastVerificationUrl);
        Assert.Equal("pt-BR", f.Mail.LastLocale);
        await f.ConfirmAsync();
        Assert.Equal(TrialState.Provisioning, f.Store.Single.State);
        Assert.Single(f.Store.Rows);
    }

    [Fact]
    public async Task AnUndeliveredConfirmationIsNotHiddenBehindTheResendCooldown()
    {
        var f = new Fixture();
        f.Mail.FailVerification = true;
        Assert.Equal(TrialRequestOutcome.Unavailable, (await f.Service.RequestAsync(new("player@example.com", "Player"))).Outcome);
        Assert.Null(f.Store.Single.VerificationTokenHash);
        f.Mail.FailVerification = false;
        // An immediate retry must really send, instead of reporting a message that never left.
        Assert.Equal(TrialRequestOutcome.Sent, (await f.Service.RequestAsync(new("player@example.com", "Player"))).Outcome);
        Assert.NotNull(f.Mail.LastVerificationUrl);
        await f.ConfirmAsync();
    }

    [Fact]
    public async Task ExpiredVerificationAndReplayCannotProvision()
    {
        var f = new Fixture();
        await f.Service.RequestAsync(new("player@example.com", "Player"));
        var token = f.VerificationToken;
        f.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(TrialConfirmationOutcome.InvalidOrExpired, (await f.Service.ConfirmAsync(token)).Outcome);
        await f.Service.RequestAsync(new("player@example.com", "Player"));
        await f.ConfirmAsync();
        Assert.Equal(TrialConfirmationOutcome.InvalidOrExpired, (await f.Service.ConfirmAsync(f.VerificationToken)).Outcome);
        Assert.Equal(0, f.Panel.CreateCalls);
    }

    [Fact]
    public async Task LostCreateResponseRecoversByExternalIdInsteadOfCreatingAnotherServer()
    {
        var f = new Fixture();
        f.Panel.Installed = true;
        f.Panel.LoseCreateResponse = true;
        await f.Service.RequestAsync(new("player@example.com", "Player"));
        await f.ConfirmAsync();
        await f.Service.ProcessDueAsync();
        Assert.Equal(1, f.Panel.CreateCalls);
        Assert.Null(f.Store.Single.StartedAt);
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        await f.Service.ProcessDueAsync();
        Assert.Equal(1, f.Panel.CreateCalls);
        Assert.Equal(TrialState.Active, f.Store.Single.State);
        Assert.Single(f.Panel.Servers);
    }

    [Fact]
    public async Task OneActualPanelAccountCannotReceiveAnotherTrialThroughADifferentEmail()
    {
        var f = new Fixture();
        f.Panel.FixedUserId = 55;
        await f.ActivateAsync();
        await f.Service.RequestAsync(new("another@example.com", "Another"));
        await f.ConfirmAsync();
        await f.Service.ProcessDueAsync();
        Assert.Equal(1, f.Panel.CreateCalls);
        Assert.Single(f.Store.Rows.Values, x => x.State == TrialState.Rejected);
        Assert.Single(f.Store.Rows.Values, x => x.State == TrialState.Active);
    }

    [Fact]
    public async Task DisabledNewRequestsStillSuspendExistingServers()
    {
        var f = new Fixture();
        await f.ActivateAsync();
        f.Options.Enabled = false;
        f.Clock.Advance(TimeSpan.FromHours(24));
        await f.Service.ProcessDueAsync();
        Assert.False(f.Service.IsAvailable);
        Assert.Equal(1, f.Panel.SuspendCalls);
        Assert.Equal(TrialState.Expired, f.Store.Single.State);
    }

    [Fact]
    public async Task PaidUpgradeKeepsTheSameServerAndWorkerCannotDeleteIt()
    {
        var f = new Fixture();
        var access = await f.ActivateAsync();
        var original = f.Store.Single.PterodactylServerId;
        var order = Guid.NewGuid();
        f.Store.Orders.Add(order);
        Assert.True((await f.Service.ReserveUpgradeAsync(f.Store.Single.Id, access, order)).Reserved);
        f.Store.PaidOrders.Add(order);
        f.Clock.Advance(TimeSpan.FromHours(97));
        await f.Service.ProcessDueAsync();
        Assert.Equal(0, f.Panel.SuspendCalls);
        Assert.Equal(0, f.Panel.DeleteCalls);
        f.Options.Enabled = false;
        var result = await f.Service.ConvertPaidOrderAsync(order, new(8192, 400, 51200));
        Assert.Equal(TrialConversionOutcome.Converted, result.Outcome);
        Assert.Equal(original, result.Server!.Id);
        Assert.Equal(original, f.Panel.LastUpgradedId);
        Assert.Equal(1, f.Panel.CreateCalls);
        f.Clock.Advance(TimeSpan.FromDays(7));
        await f.Service.ProcessDueAsync();
        Assert.Equal(TrialState.Converted, f.Store.Single.State);
        Assert.Equal(0, f.Panel.DeleteCalls);
    }

    [Fact]
    public async Task UpgradeFailureRetainsPaidProtectionAndRetryReusesTheSameSave()
    {
        var f = new Fixture();
        var access = await f.ActivateAsync();
        var order = Guid.NewGuid();
        f.Store.Orders.Add(order);
        await f.Service.ReserveUpgradeAsync(f.Store.Single.Id, access, order);
        f.Store.PaidOrders.Add(order);
        f.Panel.FailUpgrade = true;
        Assert.Equal(TrialConversionOutcome.Retry, (await f.Service.ConvertPaidOrderAsync(order, new(8192, 400, 51200))).Outcome);
        f.Clock.Advance(TimeSpan.FromHours(97));
        await f.Service.ProcessDueAsync();
        Assert.Equal(0, f.Panel.DeleteCalls);
        f.Panel.FailUpgrade = false;
        Assert.Equal(TrialConversionOutcome.Converted, (await f.Service.ConvertPaidOrderAsync(order, new(8192, 400, 51200))).Outcome);
        Assert.Equal(1, f.Panel.CreateCalls);
        Assert.Equal(2, f.Panel.UpgradeCalls);
    }

    [Fact]
    public async Task UpgradeDuringRetentionUnsuspendsTheSameServer()
    {
        var f = new Fixture();
        var access = await f.ActivateAsync();
        var original = f.Store.Single.PterodactylServerId;
        f.Clock.Advance(TimeSpan.FromHours(24));
        await f.Service.ProcessDueAsync();
        Assert.Equal(TrialState.Expired, f.Store.Single.State);
        var order = Guid.NewGuid();
        f.Store.Orders.Add(order);
        Assert.True((await f.Service.ReserveUpgradeAsync(f.Store.Single.Id, access, order)).Reserved);
        f.Store.PaidOrders.Add(order);
        var result = await f.Service.ConvertPaidOrderAsync(order, new(8192, 400, 51200));
        Assert.Equal(TrialConversionOutcome.Converted, result.Outcome);
        Assert.Equal(original, result.Server!.Id);
        Assert.Equal(1, f.Panel.ResumeCalls);
        Assert.Equal(1, f.Panel.CreateCalls);
        Assert.Equal(0, f.Panel.DeleteCalls);
    }

    [Fact]
    public async Task ASecondLatePaidOrderCannotConvertOneServerIntoTwoPaidServices()
    {
        var f = new Fixture();
        var access = await f.ActivateAsync();
        var first = Guid.NewGuid();
        f.Store.Orders.Add(first);
        await f.Service.ReserveUpgradeAsync(f.Store.Single.Id, access, first);
        f.Clock.Advance(TimeSpan.FromHours(3));
        await f.Service.ProcessDueAsync();
        Assert.Null(f.Store.Single.UpgradeOrderId); // The unpaid grace is bounded even during an active trial.
        var second = Guid.NewGuid();
        f.Store.Orders.Add(second);
        Assert.True((await f.Service.ReserveUpgradeAsync(f.Store.Single.Id, access, second)).Reserved);
        f.Store.PaidOrders.Add(second);
        f.Store.PaidOrders.Add(first);
        Assert.Equal(TrialConversionOutcome.Retry, (await f.Service.ConvertPaidOrderAsync(first, new(8192, 400, 51200))).Outcome);
        Assert.Equal(TrialConversionOutcome.Converted, (await f.Service.ConvertPaidOrderAsync(second, new(8192, 400, 51200))).Outcome);
        Assert.Equal(TrialConversionOutcome.Retry, (await f.Service.ConvertPaidOrderAsync(first, new(8192, 400, 51200))).Outcome);
        Assert.Equal(1, f.Panel.UpgradeCalls);
        Assert.Equal(1, f.Panel.CreateCalls);
    }

    [Fact]
    public async Task UnpaidReservationExpiresButTheOrderAssociationNeverFallsBackToANewServer()
    {
        var f = new Fixture();
        var access = await f.ActivateAsync();
        var order = Guid.NewGuid();
        f.Store.Orders.Add(order);
        await f.Service.ReserveUpgradeAsync(f.Store.Single.Id, access, order);
        f.Clock.Advance(TimeSpan.FromHours(24));
        await f.Service.ProcessDueAsync();
        Assert.Null(f.Store.Single.UpgradeOrderId);
        Assert.Equal(TrialState.Expired, f.Store.Single.State);
        f.Clock.Advance(TimeSpan.FromHours(72));
        await f.Service.ProcessDueAsync();
        Assert.Equal(TrialState.Deleted, f.Store.Single.State);
        f.Store.PaidOrders.Add(order);
        Assert.Equal(TrialConversionOutcome.Retry, (await f.Service.ConvertPaidOrderAsync(order, new(8192, 400, 51200))).Outcome);
        Assert.Equal(1, f.Panel.CreateCalls);
    }

    [Fact]
    public async Task ActiveLeasePreventsReservationAndStaleOwnerCannotCompleteAfterReplacement()
    {
        var f = new Fixture();
        var access = await f.ActivateAsync();
        var order = Guid.NewGuid();
        f.Store.Orders.Add(order);
        var claimed = await f.Store.ClaimAsync(f.Store.Single.Id, f.Clock.GetUtcNow(), TimeSpan.FromMinutes(5), default);
        Assert.False((await f.Service.ReserveUpgradeAsync(f.Store.Single.Id, access, order)).Reserved);
        f.Clock.Advance(TimeSpan.FromMinutes(6));
        var replacement = await f.Store.ClaimAsync(claimed!.Id, f.Clock.GetUtcNow(), TimeSpan.FromMinutes(5), default);
        claimed.State = TrialState.Deleted;
        Assert.False(await f.Store.SaveLeasedAsync(claimed, claimed.LeaseToken!.Value, f.Clock.GetUtcNow(), default));
        Assert.NotEqual(claimed.LeaseToken, replacement!.LeaseToken);
        Assert.Equal(TrialState.Active, f.Store.Single.State);
    }

    [Fact]
    public async Task ExpiredRetentionCannotBeReservedEvenBeforeTheDeleteScan()
    {
        var f = new Fixture();
        var access = await f.ActivateAsync();
        f.Clock.Advance(TimeSpan.FromHours(24));
        await f.Service.ProcessDueAsync();
        f.Clock.Advance(TimeSpan.FromHours(72));
        var order = Guid.NewGuid();
        f.Store.Orders.Add(order);
        Assert.False((await f.Service.ReserveUpgradeAsync(f.Store.Single.Id, access, order)).Reserved);
        Assert.False((await f.Service.GetByAccessTokenAsync(access))!.CanUpgrade);
    }

    [Fact]
    public async Task EmailFailuresRetryNotificationWithoutResettingTheClock()
    {
        var f = new Fixture();
        f.Mail.FailReady = true;
        await f.ActivateAsync();
        var start = f.Store.Single.StartedAt;
        Assert.Equal(TrialState.Active, f.Store.Single.State);
        Assert.Null(f.Store.Single.ReadyEmailSentAt);
        f.Mail.FailReady = false;
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        await f.Service.ProcessDueAsync();
        Assert.Equal(start, f.Store.Single.StartedAt);
        Assert.Equal(1, f.Panel.CreateCalls);
        Assert.Equal(2, f.Mail.ReadyCalls);
        Assert.NotNull(f.Store.Single.ReadyEmailSentAt);
    }

    [Fact]
    public async Task OnlyApprovedMinecraftProfilesAndVersionsReachProvisioning()
    {
        var f = new Fixture();
        f.Options.Profiles.Add(new() { Id = "java-fabric", GameSlug = "minecraft", Edition = "java", Variant = "fabric",
            Enabled = true, NestId = 1, EggId = 2, AllowedVersions = ["1.21.4"], VersionEnvironmentVariable = "MINECRAFT_VERSION" });
        Assert.Equal(TrialRequestOutcome.InvalidSelection,
            (await f.Service.RequestAsync(new("player@example.com", "Player", "minecraft", "java-fabric", "arbitrary-version"))).Outcome);
        Assert.Empty(f.Store.Rows);
        Assert.Equal(TrialRequestOutcome.Sent,
            (await f.Service.RequestAsync(new("player@example.com", "Player", "minecraft", "java-fabric", "1.21.4"))).Outcome);
        await f.ConfirmAsync();
        f.Panel.Installed = true;
        await f.Service.ProcessDueAsync();
        Assert.Equal("minecraft", f.Panel.LastRequest!.GameSlug);
        Assert.Equal("java-fabric", f.Panel.LastRequest.ProfileId);
        Assert.Equal("1.21.4", f.Panel.LastRequest.Version);
        Assert.Equal(6144, f.Panel.LastRequest.Limits.MemoryMb);
        Assert.Empty(f.Service.AvailableProfiles.Single().Environment); // Deployment fields never enter UI choices.
        Assert.Equal(0, f.Service.AvailableProfiles.Single().EggId);
    }

    [Fact]
    public async Task NoDatabaseNoPanelOrNoEmailCannotReportAnAutomaticTrialSuccess()
    {
        var f = new Fixture();
        f.Mail.Configured = false;
        Assert.False(f.Service.IsAvailable);
        Assert.Equal(TrialRequestOutcome.Unavailable, (await f.Service.RequestAsync(new("player@example.com", "Player"))).Outcome);
        Assert.Empty(f.Store.Rows);
        ITrialService unavailable = new UnavailableTrialService();
        Assert.Equal(TrialRequestOutcome.Unavailable, (await unavailable.RequestAsync(new("player@example.com", "Player"))).Outcome);
        Assert.Equal(TrialConversionOutcome.NotLinked, (await unavailable.ConvertPaidOrderAsync(Guid.NewGuid(), new(6144, 0, 25600))).Outcome);
    }

    [Fact]
    public void CommerceModelEnforcesPermanentGlobalClaimsAndDurableLeaseConcurrency()
    {
        using var db = new CommerceDbContext(new DbContextOptionsBuilder<CommerceDbContext>()
            .UseSqlServer("Server=localhost;Database=unused;Integrated Security=True").Options);
        var trial = db.Model.FindEntityType(typeof(ServerTrialRecord))!;
        Assert.True(trial.FindProperty(nameof(ServerTrialRecord.LeaseToken))!.IsConcurrencyToken);
        Assert.True(trial.GetIndexes().Single(x => x.Properties.Count == 1 && x.Properties[0].Name == nameof(ServerTrialRecord.NormalizedEmail)).IsUnique);
        var panel = trial.GetIndexes().Single(x => x.Properties.Count == 1 && x.Properties[0].Name == nameof(ServerTrialRecord.PterodactylUserId));
        Assert.True(panel.IsUnique);
        Assert.Equal("[pterodactyl_user_id] IS NOT NULL", panel.GetFilter());
        Assert.All(trial.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        var links = db.Model.FindEntityType(typeof(TrialUpgradeOrderRecord))!;
        Assert.Equal(nameof(TrialUpgradeOrderRecord.OrderId), links.FindPrimaryKey()!.Properties.Single().Name);
        Assert.All(links.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
    }

    [Fact]
    public void LifecycleLeaseMustOutlastThePanelTimeoutWithAnExplicitMargin()
    {
        var validator = new TrialOptionsValidator(Options.Create(new PterodactylOptions { TimeoutSeconds = 300 }));
        Assert.True(validator.Validate(null, new TrialOptions { LeaseMinutes = 5 }).Failed);
        Assert.True(validator.Validate(null, new TrialOptions { LeaseMinutes = 6 }).Succeeded);
        Assert.True(new TrialOptionsValidator().Validate(null, new TrialOptions()).Succeeded);
    }

    private sealed class Fixture
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        public MemoryStore Store { get; } = new();
        public FakePanel Panel { get; } = new();
        public FakeMail Mail { get; } = new();
        public TrialOptions Options { get; } = new() { Enabled = true };
        public TrialService Service { get; }
        public string VerificationToken => Uri.UnescapeDataString(Mail.LastVerificationUrl!.Split("?verify=", StringSplitOptions.None)[1]);
        public Fixture() => Service = new(Store, Panel, Mail, new Monitor<TrialOptions>(Options),
            Microsoft.Extensions.Options.Options.Create(new SiteOptions { BaseUrl = "https://hosting.example.com" }),
            new Monitor<PterodactylOptions>(new() { BaseUrl = "https://panel.example.com" }), Clock, NullLogger<TrialService>.Instance);
        public async Task<string> ConfirmAsync()
        {
            var result = await Service.ConfirmAsync(VerificationToken);
            Assert.Equal(TrialConfirmationOutcome.Confirmed, result.Outcome);
            return result.AccessToken!;
        }
        public async Task<string> ActivateAsync()
        {
            Panel.Installed = true;
            await Service.RequestAsync(new("player@example.com", "Player"));
            var access = await ConfirmAsync();
            await Service.ProcessDueAsync();
            return access;
        }
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeMail : ITrialEmailSender
    {
        public bool Configured { get; set; } = true;
        public bool IsConfigured => Configured;
        public string? LastVerificationUrl { get; private set; }
        public string? LastLocale { get; private set; }
        public int ReadyCalls { get; private set; }
        public int ExpiredCalls { get; private set; }
        public int DeletedCalls { get; private set; }
        public bool FailReady { get; set; }
        public bool FailVerification { get; set; }
        public Task SendVerificationAsync(string email, string name, string url, string locale, CancellationToken ct)
        {
            if (FailVerification) throw new IOException("Relay rejected the message");
            LastVerificationUrl = url; LastLocale = locale; return Task.CompletedTask;
        }
        public Task SendReadyAsync(string email, string panelUrl, DateTimeOffset expiresAt, string locale, CancellationToken ct)
        { ReadyCalls++; if (FailReady) throw new IOException("Delivery unavailable"); return Task.CompletedTask; }
        public Task SendExpiredAsync(string email, DateTimeOffset deleteAfter, string locale, CancellationToken ct)
        { ExpiredCalls++; return Task.CompletedTask; }
        public Task SendDeletedAsync(string email, string locale, CancellationToken ct)
        { DeletedCalls++; return Task.CompletedTask; }
    }

    private sealed class FakePanel : ITrialPanelGateway
    {
        public bool Configured { get; set; } = true;
        public bool IsConfigured => Configured;
        public bool Installed { get; set; }
        public bool LoseCreateResponse { get; set; }
        public bool FailUpgrade { get; set; }
        public int? FixedUserId { get; set; }
        public int CreateCalls { get; private set; }
        public int SuspendCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int UpgradeCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int? LastDeletedId { get; private set; }
        public int? LastUpgradedId { get; private set; }
        public TrialProvisioningRequest? LastRequest { get; private set; }
        public Dictionary<string, TrialPanelServer> Servers { get; } = [];
        private readonly Dictionary<string, int> _users = [];
        public bool CanProvision(string game, string? profile = null, string? version = null) => Configured;
        public Task<TrialPanelUser> ResolveUserAsync(string email, string name, CancellationToken ct)
        {
            if (!_users.TryGetValue(email, out var id)) { id = FixedUserId ?? _users.Count + 1; _users[email] = id; }
            return Task.FromResult(new TrialPanelUser(id, email));
        }
        public Task<TrialPanelServer?> FindServerAsync(string externalId, CancellationToken ct) =>
            Task.FromResult(Servers.TryGetValue(externalId, out var found) ? found with { Installed = Installed } : null);
        public Task<TrialPanelServer> CreateServerAsync(TrialProvisioningRequest request, CancellationToken ct)
        {
            CreateCalls++; LastRequest = request;
            var result = new TrialPanelServer(Servers.Count + 1, "server" + (Servers.Count + 1), Guid.NewGuid().ToString(), Installed, false);
            Servers.Add(request.ExternalId, result);
            if (LoseCreateResponse) { LoseCreateResponse = false; throw new IOException("Response lost after create"); }
            return Task.FromResult(result);
        }
        public Task<TrialPanelServer?> InspectServerAsync(int id, CancellationToken ct) =>
            Task.FromResult(Servers.Values.SingleOrDefault(x => x.Id == id) is { } found ? found with { Installed = Installed } : null);
        public Task SuspendAsync(int id, CancellationToken ct)
        {
            SuspendCalls++;
            var key = Servers.Single(x => x.Value.Id == id).Key;
            Servers[key] = Servers[key] with { Suspended = true };
            return Task.CompletedTask;
        }
        public Task DeleteAsync(int id, CancellationToken ct)
        {
            DeleteCalls++; LastDeletedId = id;
            var entry = Servers.SingleOrDefault(x => x.Value.Id == id);
            if (entry.Key is not null) Servers.Remove(entry.Key);
            return Task.CompletedTask;
        }
        public Task UpgradeAsync(int id, TrialResourceLimits limits, CancellationToken ct)
        {
            UpgradeCalls++; LastUpgradedId = id;
            if (FailUpgrade) throw new IOException("Temporary build update failure");
            return Task.CompletedTask;
        }
        public Task ResumeAsync(int id, CancellationToken ct)
        {
            ResumeCalls++;
            var key = Servers.Single(x => x.Value.Id == id).Key;
            Servers[key] = Servers[key] with { Suspended = false };
            return Task.CompletedTask;
        }
    }

    /// <summary>Deterministic repository fixture, including independent copies and compare-and-set lease semantics.</summary>
    private sealed class MemoryStore : ITrialStore
    {
        public bool IsConfigured => true;
        public Dictionary<Guid, ServerTrialRecord> Rows { get; } = [];
        public HashSet<Guid> Orders { get; } = [];
        public HashSet<Guid> PaidOrders { get; } = [];
        private readonly Dictionary<Guid, Guid> _orderTrials = [];
        public ServerTrialRecord Single => Clone(Rows.Values.Single());
        private static ServerTrialRecord Clone(ServerTrialRecord x) => JsonSerializer.Deserialize<ServerTrialRecord>(JsonSerializer.Serialize(x))!;
        private Task<ServerTrialRecord?> Find(Func<ServerTrialRecord, bool> filter) =>
            Task.FromResult(Rows.Values.SingleOrDefault(filter) is { } value ? Clone(value) : null);
        public Task<ServerTrialRecord?> FindByEmailAsync(string email, CancellationToken ct) => Find(x => x.NormalizedEmail == email);
        public Task<ServerTrialRecord?> FindByVerificationAsync(string hash, CancellationToken ct) => Find(x => x.VerificationTokenHash == hash);
        public Task<ServerTrialRecord?> FindByAccessAsync(string hash, CancellationToken ct) => Find(x => x.AccessTokenHash == hash);
        public Task<ServerTrialRecord?> FindByOrderAsync(Guid order, CancellationToken ct) =>
            Find(x => _orderTrials.TryGetValue(order, out var trial) && x.Id == trial);
        public Task<bool> AddPendingAsync(ServerTrialRecord x, CancellationToken ct)
        {
            if (Rows.Values.Any(r => r.NormalizedEmail == x.NormalizedEmail)) return Task.FromResult(false);
            Rows.Add(x.Id, Clone(x)); return Task.FromResult(true);
        }
        public Task<bool> RefreshVerificationAsync(ServerTrialRecord x, DateTimeOffset now, CancellationToken ct)
        {
            var row = Rows[x.Id];
            if (!Free(row, now)) return Task.FromResult(false);
            row.VerificationTokenHash = x.VerificationTokenHash; row.VerificationExpiresAt = x.VerificationExpiresAt;
            row.Locale = x.Locale; row.GameSlug = x.GameSlug; row.ProfileId = x.ProfileId; row.Version = x.Version;
            row.UpdatedAt = now; return Task.FromResult(true);
        }
        public Task ClearVerificationAsync(Guid id, string hash, CancellationToken ct)
        {
            var row = Rows[id];
            if (row.VerificationTokenHash == hash) { row.VerificationTokenHash = null; row.VerificationExpiresAt = null; }
            return Task.CompletedTask;
        }
        public Task<bool> ConfirmAsync(Guid id, string hash, string access, DateTimeOffset now, CancellationToken ct)
        {
            var row = Rows[id];
            if (row.VerificationTokenHash != hash || row.VerificationExpiresAt <= now || !Free(row, now)) return Task.FromResult(false);
            if (row.VerifiedAt is null) { row.State = TrialState.Provisioning; row.VerifiedAt = now; }
            row.AccessTokenHash = access; row.VerificationTokenHash = null; row.VerificationExpiresAt = null;
            row.UpdatedAt = now; return Task.FromResult(true);
        }
        public Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>(Rows.Values.Where(x => Free(x, now)
                && (x.NextAttemptAt is null || x.NextAttemptAt <= now)
                && (x.State is TrialState.Provisioning or TrialState.Installing or TrialState.Deleting
                    || (x.State == TrialState.Active && (x.ExpiresAt <= now || x.ReadyEmailSentAt is null))
                    || (x.State == TrialState.Expired && (x.DeleteAfter <= now || x.ExpiredEmailSentAt is null))
                    || (x.State == TrialState.Deleted && x.DeletedEmailSentAt is null)
                    || (x.UpgradeOrderId is not null && x.ReservationExpiresAt <= now))).Select(x => x.Id).ToArray());
        private static bool Free(ServerTrialRecord row, DateTimeOffset now) => row.LeaseToken is null || row.LeaseExpiresAt <= now;
        public Task<ServerTrialRecord?> ClaimAsync(Guid id, DateTimeOffset now, TimeSpan duration, CancellationToken ct)
        {
            var row = Rows[id];
            if (!Free(row, now) || row.State is TrialState.Converted or TrialState.PendingVerification or TrialState.Rejected)
                return Task.FromResult<ServerTrialRecord?>(null);
            row.LeaseToken = Guid.NewGuid(); row.LeaseExpiresAt = now + duration;
            return Task.FromResult<ServerTrialRecord?>(Clone(row));
        }
        public Task<bool> RenewAsync(Guid id, Guid token, DateTimeOffset now, TimeSpan duration, CancellationToken ct)
        {
            var row = Rows[id];
            if (row.LeaseToken != token || row.LeaseExpiresAt <= now) return Task.FromResult(false);
            row.LeaseExpiresAt = now + duration; return Task.FromResult(true);
        }
        public Task<bool> SaveLeasedAsync(ServerTrialRecord x, Guid token, DateTimeOffset now, CancellationToken ct)
        {
            var row = Rows[x.Id];
            if (row.LeaseToken != token || row.LeaseExpiresAt <= now) return Task.FromResult(false);
            if (x.PterodactylUserId is not null && Rows.Values.Any(r => r.Id != x.Id && r.PterodactylUserId == x.PterodactylUserId))
                throw new TrialAccountAlreadyClaimedException();
            var next = Clone(x);
            next.AccessTokenHash = row.AccessTokenHash; next.VerificationTokenHash = row.VerificationTokenHash;
            next.VerificationExpiresAt = row.VerificationExpiresAt; next.Locale = row.Locale;
            Rows[x.Id] = next; return Task.FromResult(true);
        }
        public Task ReleaseLeaseAsync(Guid id, Guid token, CancellationToken ct)
        {
            if (Rows[id].LeaseToken == token) { Rows[id].LeaseToken = null; Rows[id].LeaseExpiresAt = null; }
            return Task.CompletedTask;
        }
        public Task<bool> ReserveUpgradeAsync(Guid id, string accessHash, Guid order, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct)
        {
            var row = Rows[id];
            if (!Orders.Contains(order) || row.AccessTokenHash != accessHash || !Free(row, now)
                || row.State is not (TrialState.Active or TrialState.Expired) || row.PterodactylServerId is null
                || (row.State == TrialState.Expired && row.DeleteAfter <= now)
                || (row.UpgradeOrderId is not null && row.UpgradeOrderId != order)) return Task.FromResult(false);
            row.UpgradeOrderId = order; row.ReservationExpiresAt = expires; _orderTrials[order] = id;
            return Task.FromResult(true);
        }
        public Task ReleaseUpgradeAsync(Guid order, CancellationToken ct)
        {
            foreach (var row in Rows.Values.Where(x => x.UpgradeOrderId == order && x.State != TrialState.Converted && x.LeaseToken is null))
                if (!PaidOrders.Contains(order)) { row.UpgradeOrderId = null; row.ReservationExpiresAt = null; }
            return Task.CompletedTask;
        }
        public Task<bool> IsPaidOrderAsync(Guid order, CancellationToken ct) => Task.FromResult(PaidOrders.Contains(order));
        public Task<Guid?> FindPaidUpgradeAsync(Guid trial, CancellationToken ct) =>
            Task.FromResult(_orderTrials.FirstOrDefault(x => x.Value == trial && PaidOrders.Contains(x.Key)).Key is var key && key != Guid.Empty ? (Guid?)key : null);
        public Task<bool> ClearUnpaidReservationAsync(Guid id, Guid token, DateTimeOffset now, CancellationToken ct)
        {
            var row = Rows[id];
            if (row.LeaseToken == token && row.UpgradeOrderId is { } order && !PaidOrders.Contains(order) && row.ReservationExpiresAt <= now)
            { row.UpgradeOrderId = null; row.ReservationExpiresAt = null; return Task.FromResult(true); }
            return Task.FromResult(false);
        }
        public Task<bool> BeginDeleteAsync(Guid id, Guid token, DateTimeOffset now, CancellationToken ct)
        {
            var row = Rows[id];
            if (row.LeaseToken != token || row.State != TrialState.Expired || row.DeleteAfter > now || row.UpgradeOrderId is not null
                || _orderTrials.Any(x => x.Value == id && PaidOrders.Contains(x.Key))) return Task.FromResult(false);
            row.State = TrialState.Deleting; return Task.FromResult(true);
        }
    }
}
