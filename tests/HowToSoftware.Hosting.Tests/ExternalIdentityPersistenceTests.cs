using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Identity;
using HowToSoftware.Hosting.Services.Subscriptions;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HowToSoftware.Hosting.Tests;

public sealed class ExternalIdentityPersistenceTests
{
    private const string DiscordId = "123456789012345678";
    [Fact]
    public async Task DisabledProvidersNeverReadTheNewSchemaOrChangeNativeAccess()
    {
        var access = new NoNativeCalls();
        var service = new ExternalIdentityService(new NoDatabaseCalls(), Options.Create(new ExternalIdentityOptions()), access, TimeProvider.System);
        Assert.Empty(await service.ConnectionsAsync(null, default));
        Assert.Null(await service.BeginAsync("discord", "link", new string('A', 64), default));
        Assert.Null(await service.BeginAsync("active-directory", "login", null, default));
        Assert.Null(await service.PendingAsync(Guid.NewGuid(), new string('A', 64), null, default));
        Assert.False((await service.CompleteAsync(Guid.NewGuid(), new string('A', 64), null, default)).Success);
        Assert.False(await service.UnlinkAsync("discord", null, default));
    }
    [Fact]
    public async Task LinkingNeedsBothOriginalNativeSessionAndOriginalBrowserAndExplicitConfirmation()
    {
        await using var h = await Harness.Create();
        var (owner, session) = await h.Customer("owner@example.test");
        var (_, otherSession) = await h.Customer("other@example.test");
        Assert.Null(await h.Service.BeginAsync("discord", "link", null, default));
        var flow = (await h.Service.BeginAsync("discord", "link", session, default))!;
        Assert.Null(await h.Service.PendingAsync(flow.Id, flow.BrowserSecret, session, default));
        Assert.True(await h.Verify(flow.Id, DiscordId));
        Assert.NotNull(await h.Service.PendingAsync(flow.Id, flow.BrowserSecret, session, default));
        Assert.Null(await h.Service.PendingAsync(flow.Id, new string('F', 64), session, default));
        Assert.False((await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, otherSession, default)).Success);
        await using (var db = h.Factory.CreateDbContext()) Assert.Empty(await db.CustomerExternalIdentities.ToListAsync());
        var result = await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, session, default);
        Assert.True(result.Success); Assert.Null(result.SessionToken);
        await using (var db = h.Factory.CreateDbContext())
        {
            var identity = await db.CustomerExternalIdentities.SingleAsync();
            Assert.Equal(owner, identity.CustomerAccessId);
            Assert.Equal(DiscordId, identity.Subject);
        }
        Assert.False((await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, session, default)).Success);
    }
    [Fact]
    public async Task SessionRotationInvalidatesAnOutstandingLinkEvenForTheSameCustomer()
    {
        await using var h = await Harness.Create();
        var (owner, session) = await h.Customer("owner@example.test");
        var flow = (await h.Service.BeginAsync("discord", "link", session, default))!;
        await h.Verify(flow.Id, DiscordId);
        var newSession = CustomerAccessService.NewToken();
        await using (var db = h.Factory.CreateDbContext())
            await db.CustomerAccess.Where(x => x.Id == owner).ExecuteUpdateAsync(u => u.SetProperty(x => x.SessionHash, CustomerAccessService.Hash(newSession)));
        Assert.False((await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, newSession, default)).Success);
        Assert.False((await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, session, default)).Success);
    }
    [Fact]
    public async Task TheSameDiscordIdentityCannotBeAttachedToTwoCustomers()
    {
        await using var h = await Harness.Create();
        var (first, firstSession) = await h.Customer("first@example.test");
        var (_, secondSession) = await h.Customer("second@example.test");
        Assert.True(await h.Link(firstSession, DiscordId));
        Assert.False(await h.Link(secondSession, DiscordId));
        await using var db = h.Factory.CreateDbContext();
        Assert.Equal(first, (await db.CustomerExternalIdentities.SingleAsync()).CustomerAccessId);
    }
    [Fact]
    public async Task ReplacingAnIdentityRequiresUnlinkingAndNeverTransfersAnotherCustomersLink()
    {
        await using var h = await Harness.Create();
        var (_, session) = await h.Customer("owner@example.test");
        var (_, other) = await h.Customer("other@example.test");
        Assert.True(await h.Link(session, DiscordId));
        Assert.False(await h.Link(session, "223456789012345678"));
        await h.Service.UnlinkAsync("discord", other, default);
        Assert.Single(await h.Service.ConnectionsAsync(session, default));
        Assert.True(await h.Service.UnlinkAsync("discord", session, default));
        Assert.Empty(await h.Service.ConnectionsAsync(session, default));
        Assert.True(await h.Link(session, "223456789012345678"));
    }
    [Fact]
    public async Task LoginOnlyUsesAnExplicitBindingAndRotatesTheNativeSessionWithoutPersistingTokens()
    {
        await using var h = await Harness.Create();
        var (_, session) = await h.Customer("owner@example.test");
        await h.Link(session, DiscordId);
        var flow = (await h.Service.BeginAsync("discord", "login", null, default))!;
        await h.Verify(flow.Id, DiscordId);
        Assert.False((await h.Service.CompleteAsync(flow.Id, new string('F', 64), null, default)).Success);
        var result = await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, null, default);
        Assert.True(result.Success); Assert.NotNull(result.SessionToken);
        Assert.Equal("owner@example.test", await h.Native.OwnerAsync(result.SessionToken, default));
        Assert.Null(await h.Native.OwnerAsync(session, default));
        await using var db = h.Factory.CreateDbContext();
        var record = await db.ExternalIdentityFlows.SingleAsync(x => x.Id == flow.Id);
        Assert.NotEqual(flow.BrowserSecret, record.BrowserSecretHash);
        Assert.DoesNotContain(db.Model.FindEntityType(typeof(CustomerExternalIdentity))!.GetProperties(),
            x => x.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.False((await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, null, default)).Success);
    }
    [Fact]
    public async Task UnlinkedExternalLoginNeverCreatesOrMergesAnHtsCustomer()
    {
        await using var h = await Harness.Create();
        var flow = (await h.Service.BeginAsync("discord", "login", null, default))!;
        await h.Verify(flow.Id, DiscordId);
        Assert.False((await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, null, default)).Success);
        await using var db = h.Factory.CreateDbContext();
        Assert.Empty(await db.CustomerAccess.ToListAsync());
        Assert.Empty(await db.CustomerExternalIdentities.ToListAsync());
    }
    [Fact]
    public async Task LinkAndLoginFlagsAreIndependentAndRecheckedAtCompletion()
    {
        await using var h = await Harness.Create();
        var (_, session) = await h.Customer("owner@example.test");
        h.Options.Discord.LoginEnabled = false;
        Assert.Null(await h.Service.BeginAsync("discord", "login", null, default));
        Assert.True(await h.Link(session, DiscordId));
        h.Options.Discord.LoginEnabled = true;
        var flow = (await h.Service.BeginAsync("discord", "login", null, default))!;
        await h.Verify(flow.Id, DiscordId);
        h.Options.Discord.LoginEnabled = false;
        Assert.False((await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, null, default)).Success);
    }
    [Fact]
    public async Task ExpiredAndReplayedProviderResponsesCannotChangeARecordedIdentity()
    {
        await using var h = await Harness.Create();
        var flow = (await h.Service.BeginAsync("discord", "login", null, default))!;
        Assert.True(await h.Verify(flow.Id, DiscordId));
        Assert.False(await h.Verify(flow.Id, "223456789012345678"));
        h.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await h.Service.PendingAsync(flow.Id, flow.BrowserSecret, null, default));
        Assert.False((await h.Service.CompleteAsync(flow.Id, flow.BrowserSecret, null, default)).Success);
        Assert.False(await h.Verify(flow.Id, DiscordId));
    }
    [Theory]
    [InlineData("https://attacker.test", DiscordId)]
    [InlineData("https://discord.com", "not-an-id")]
    [InlineData("https://discord.com", "123")]
    [InlineData("https://discord.com", "99999999999999999999")]
    public async Task MalformedOrForeignProviderSubjectsCannotBeVerified(string issuer, string subject)
    {
        await using var h = await Harness.Create();
        var flow = (await h.Service.BeginAsync("discord", "login", null, default))!;
        Assert.False(await h.Service.VerifyAsync(flow.Id, "discord", issuer, subject, "Same display name", default));
    }
    [Fact]
    public async Task UnlinkImmediatelyPreventsNewExternalLoginsButKeepsEmailAndTheCurrentSession()
    {
        await using var h = await Harness.Create();
        var (_, session) = await h.Customer("owner@example.test");
        await h.Link(session, DiscordId);
        var pending = (await h.Service.BeginAsync("discord", "login", null, default))!;
        await h.Verify(pending.Id, DiscordId);
        await h.Service.UnlinkAsync("discord", session, default);
        Assert.False((await h.Service.CompleteAsync(pending.Id, pending.BrowserSecret, null, default)).Success);
        Assert.Equal("owner@example.test", await h.Native.OwnerAsync(session, default));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private SqliteConnection _connection = new("Data Source=:memory:");
        private ServiceProvider _services = null!;
        public Factory Factory = null!;
        public FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        public ExternalIdentityOptions Options = new() { Discord = new() { Enabled = true, LoginEnabled = true } };
        public ExternalIdentityService Service = null!;
        public CustomerAccessService Native = null!;
        public static async Task<Harness> Create()
        {
            var h = new Harness(); await h._connection.OpenAsync();
            h._connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            h.Factory = new(new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(h._connection).Options);
            await using (var db = h.Factory.CreateDbContext())
                await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT", StringComparison.OrdinalIgnoreCase));
            h._services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            h.Native = new(h.Factory, Microsoft.Extensions.Options.Options.Create(new CustomerAreaOptions { Enabled = true }),
                Microsoft.Extensions.Options.Options.Create(new SmtpOptions { Host = "smtp.invalid", FromAddress = "sender@example.test" }),
                Microsoft.Extensions.Options.Options.Create(new SiteOptions()), new NoMail(),
                h._services.GetRequiredService<IStringLocalizer<CustomerText>>(), h.Clock);
            h.Service = new(h.Factory, Microsoft.Extensions.Options.Options.Create(h.Options), h.Native, h.Clock);
            return h;
        }
        public async Task<(Guid Id, string Session)> Customer(string email)
        {
            var id = Guid.NewGuid(); var session = CustomerAccessService.NewToken();
            await using var db = Factory.CreateDbContext();
            db.CustomerAccess.Add(new() { Id = id, NormalizedEmail = email,
                ChallengeHash = CustomerAccessService.Hash(CustomerAccessService.NewToken()), SessionHash = CustomerAccessService.Hash(session),
                CreatedAt = Clock.GetUtcNow(), UsedAt = Clock.GetUtcNow(), ExpiresAt = Clock.GetUtcNow(), SessionExpiresAt = Clock.GetUtcNow().AddHours(1) });
            await db.SaveChangesAsync(); return (id, session);
        }
        public Task<bool> Verify(Guid id, string subject) => Service.VerifyAsync(id, "discord", ExternalProviders.DiscordIssuer, subject, "Discord user", default);
        public async Task<bool> Link(string session, string subject)
        {
            var flow = (await Service.BeginAsync("discord", "link", session, default))!;
            await Verify(flow.Id, subject);
            return (await Service.CompleteAsync(flow.Id, flow.BrowserSecret, session, default)).Success;
        }
        public async ValueTask DisposeAsync() { await _connection.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Factory(DbContextOptions<CommerceDbContext> options) : IDbContextFactory<CommerceDbContext>
    { public CommerceDbContext CreateDbContext() => new(options); }
    private sealed class NoDatabaseCalls : IDbContextFactory<CommerceDbContext>
    { public CommerceDbContext CreateDbContext() => throw new InvalidOperationException("Disabled providers must not read SQL."); }
    private sealed class NoMail : ITrialMailTransport
    { public Task DeliverAsync(TrialMailMessage message, CancellationToken ct) => throw new InvalidOperationException("External identity must never send SMTP in tests."); }
    private sealed class NoNativeCalls : ICustomerAccess
    {
        public bool Enabled => true;
        public Task<bool> RequestAsync(string email, CancellationToken ct) => throw new InvalidOperationException();
        public Task<string?> ConfirmAsync(string token, CancellationToken ct) => throw new InvalidOperationException();
        public Task<string?> OwnerAsync(string? session, CancellationToken ct) => throw new InvalidOperationException();
        public Task RevokeAsync(string? session, CancellationToken ct) => throw new InvalidOperationException();
    }
}
