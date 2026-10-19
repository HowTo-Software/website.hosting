using System.Text.RegularExpressions;
using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services.Subscriptions;
using HowToSoftware.Hosting.Services.Trials;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HowToSoftware.Hosting.Tests;

/// <summary>Real relational storage with no remote database or external integrations.</summary>
public sealed class CustomerAreaPersistenceTests
{
    [Fact]
    public async Task SubscriptionPlanPriceMappingsAreIsolatedByCurrency()
    {
        await using var h = await DatabaseHarness.Create();
        var service = await h.Seed();
        await using (var db = h.Factory.CreateDbContext())
        {
            var plan = await db.HostingPlans.SingleAsync();
            plan.Active = true;
            db.PlanBillingPrices.AddRange(
                new PlanBillingPrice { Id = Guid.NewGuid(), PlanId = plan.Id, BillingPeriod = "monthly",
                    Currency = "usd", AmountCents = 799, Active = true, StripePriceId = "price_usd" },
                new PlanBillingPrice { Id = Guid.NewGuid(), PlanId = plan.Id, BillingPeriod = "monthly",
                    Currency = "BRL", AmountCents = 5194, Active = true, StripePriceId = "price_brl" });
            await db.SaveChangesAsync();
        }
        var store = new SubscriptionStore(h.Factory);
        Assert.Equal("price_usd", Assert.Single(await store.ListPlansAsync(service.GameId, "monthly", default)).StripePriceId);
        Assert.Equal("price_brl", Assert.Single(await store.ListPlansAsync(service.GameId, "monthly", "brl", default)).StripePriceId);
        Assert.Empty(await store.ListPlansAsync(service.GameId, "monthly", "eur", default));
    }

    [Fact]
    public async Task AccessPersistsOnlyHashesAndConsumesChallengeExactlyOnce()
    {
        await using var h = await DatabaseHarness.Create();
        await h.Access.RequestAsync("Owner@Example.Test", default);
        var token = h.Mail.Token;
        await using (var db = h.Factory.CreateDbContext())
        {
            var row = await db.CustomerAccess.SingleAsync();
            Assert.Equal("owner@example.test", row.NormalizedEmail);
            Assert.NotEqual(token, row.ChallengeHash);
            Assert.Equal(64, row.ChallengeHash.Length);
            Assert.Null(row.SessionHash);
        }
        var session = await h.Access.ConfirmAsync(token, default);
        Assert.NotNull(session);
        Assert.NotEqual(token, session);
        Assert.Null(await h.Access.ConfirmAsync(token, default));
        Assert.Equal("owner@example.test", await h.Access.OwnerAsync(session, default));
        Assert.Null(await h.Access.OwnerAsync(token, default));
        await h.Access.RevokeAsync(session, default);
        Assert.Null(await h.Access.OwnerAsync(session, default));
    }

    [Fact]
    public async Task ChallengeAndSessionLifetimesAreEnforcedInTheDatabase()
    {
        await using var h = await DatabaseHarness.Create();
        await h.Access.RequestAsync("owner@example.test", default);
        var old = h.Mail.Token;
        h.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(await h.Access.ConfirmAsync(old, default));
        await h.Access.RequestAsync("owner@example.test", default);
        var session = await h.Access.ConfirmAsync(h.Mail.Token, default);
        Assert.NotNull(session);
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(await h.Access.OwnerAsync(session, default));
    }

    [Fact]
    public async Task MailCooldownIsAtomicAndNewChallengeInvalidatesPreviousLink()
    {
        await using var h = await DatabaseHarness.Create();
        await h.Access.RequestAsync("owner@example.test", default);
        var previous = h.Mail.Token;
        for (var i = 0; i < 10; i++) await h.Access.RequestAsync("owner@example.test", default);
        Assert.Single(h.Mail.Messages);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.Access.RequestAsync("owner@example.test", default);
        Assert.Equal(2, h.Mail.Messages.Count);
        Assert.Null(await h.Access.ConfirmAsync(previous, default));
        Assert.NotNull(await h.Access.ConfirmAsync(h.Mail.Token, default));
    }
    [Fact]
    public async Task RequestingAnotherEmailDoesNotRevokeAnAlreadyVerifiedSession()
    {
        await using var h = await DatabaseHarness.Create();
        await h.Access.RequestAsync("owner@example.test", default);
        var session = await h.Access.ConfirmAsync(h.Mail.Token, default);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.Access.RequestAsync("owner@example.test", default);
        Assert.Equal("owner@example.test", await h.Access.OwnerAsync(session, default));
        var rotated = await h.Access.ConfirmAsync(h.Mail.Token, default);
        Assert.NotNull(rotated);
        Assert.Null(await h.Access.OwnerAsync(session, default));
        Assert.Equal("owner@example.test", await h.Access.OwnerAsync(rotated, default));
    }

    [Fact]
    public async Task InvalidEmailsAndMalformedTokensCannotTriggerMailOrDatabaseAuthentication()
    {
        await using var h = await DatabaseHarness.Create();
        foreach (var email in new[] { "", "bad", "Victim <victim@example.test>", "a@example.test\r\nBcc:b@example.test" })
            await h.Access.RequestAsync(email, default);
        Assert.Empty(h.Mail.Messages);
        Assert.Null(await h.Access.ConfirmAsync(new string('x', 64), default));
        Assert.Null(await h.Access.OwnerAsync(new string('F', 128), default));
    }

    [Fact]
    public async Task PerServiceUniquenessAndLeaseFencingSurvivePersistence()
    {
        await using var h = await DatabaseHarness.Create();
        var seeded = await h.Seed();
        var store = new SubscriptionStore(h.Factory);
        SubscriptionPlanChange Change() => new() { Id = Guid.NewGuid(), HostingServiceId = seeded.Id, TargetPlanId = seeded.Plan.Id,
            StripeSubscriptionId = "sub_test", StripeCustomerId = "cus_test", State = PlanChangeState.Quoted,
            CreatedAt = h.Clock.GetUtcNow(), UpdatedAt = h.Clock.GetUtcNow(), QuoteExpiresAt = h.Clock.GetUtcNow().AddMinutes(5) };
        var first = Change();
        Assert.True(await store.AddChangeAsync(first, default));
        Assert.False(await store.AddChangeAsync(Change(), default));
        var claimed = await store.ClaimAsync(first.Id, h.Clock.GetUtcNow(), default);
        Assert.NotNull(claimed);
        Assert.Null(await store.ClaimAsync(first.Id, h.Clock.GetUtcNow(), default));
        var staleToken = claimed.LeaseToken!.Value;
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        var current = await store.ClaimAsync(first.Id, h.Clock.GetUtcNow(), default);
        Assert.NotEqual(staleToken, current!.LeaseToken);
        claimed.State = PlanChangeState.Applied; claimed.Active = false; claimed.LeaseToken = null;
        Assert.False(await store.SaveAsync(claimed, staleToken, default));
        var liveToken = current.LeaseToken!.Value;
        current.State = PlanChangeState.Cancelled; current.Active = false; current.LeaseToken = null;
        Assert.True(await store.SaveAsync(current, liveToken, default));
        Assert.True(await store.AddChangeAsync(Change(), default));
    }

    [Fact]
    public async Task OwnershipJoinOnlyReturnsTheVerifiedEmailsServicesAndSupportRequests()
    {
        await using var h = await DatabaseHarness.Create();
        var service = await h.Seed();
        var store = new SubscriptionStore(h.Factory);
        Assert.Single(await store.ListOwnedAsync("owner@example.test", default));
        Assert.Empty(await store.ListOwnedAsync("other@example.test", default));
        var stripe = new SubscriptionManagementTests.FakeStripe(h.Clock.GetUtcNow(), false);
        var workspace = new CustomerWorkspace(store, stripe, h.Factory, h.Mail, Options.Create(new SiteOptions()),
            h.Clock, NullLogger<CustomerWorkspace>.Instance);
        Assert.True(await workspace.SupportAsync("owner@example.test", "A support request", "Please help with my service.", "en", default));
        var owned = await workspace.LoadAsync("owner@example.test", "overview", default);
        var other = await workspace.LoadAsync("other@example.test", "overview", default);
        Assert.Single(owned.Tickets);
        Assert.Single(owned.Services);
        Assert.Empty(other.Tickets); Assert.Empty(other.Services); Assert.Empty(other.Invoices);
        Assert.False(await workspace.SupportAsync("owner@example.test", "bad\r\nsubject", "Message long enough.", "en", default));
        await workspace.DeliverPendingAsync(default);
        await using var db = h.Factory.CreateDbContext();
        Assert.NotNull((await db.CustomerSupport.SingleAsync()).NotifiedAt);
    }

    [Theory]
    [InlineData("https://invoice.stripe.com/i/test", true)]
    [InlineData("https://pay.stripe.com/invoice/test/pdf", true)]
    [InlineData("https://invoice.stripe.com.attacker.test/i/test", false)]
    [InlineData("http://invoice.stripe.com/i/test", false)]
    [InlineData("https://invoice.stripe.com@attacker.test/i/test", false)]
    [InlineData("javascript:alert(1)", false)]
    public void InvoiceLinksMustBeHttpsAndUseExactStripeHosts(string url, bool valid) =>
        Assert.Equal(valid, CustomerWorkspace.SafeStripeLink(url) is not null);

    private sealed class DatabaseHarness : IAsyncDisposable
    {
        public SqliteConnection Connection = new("Data Source=:memory:");
        public TestFactory Factory = null!;
        public RecordingMail Mail = new();
        public FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        public CustomerAccessService Access = null!;
        private ServiceProvider _services = null!;
        public static async Task<DatabaseHarness> Create()
        {
            var h = new DatabaseHarness();
            await h.Connection.OpenAsync();
            h.Connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            h.Factory = new(new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(h.Connection).Options);
            await using (var db = h.Factory.CreateDbContext())
            {
                // Existing SQL Server JSON columns explicitly use nvarchar(max). Adapt only
                // that DDL spelling for this hermetic provider; the production model is unchanged.
                var ddl = db.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT", StringComparison.OrdinalIgnoreCase);
                await db.Database.ExecuteSqlRawAsync(ddl);
            }
            h._services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            h.Access = new(h.Factory, Options.Create(new CustomerAreaOptions { Enabled = true }),
                Options.Create(new SmtpOptions { Host = "smtp.invalid", FromAddress = "sender@example.test" }),
                Options.Create(new SiteOptions { BaseUrl = "https://host.example.test" }),
                h.Mail, h._services.GetRequiredService<IStringLocalizer<CustomerText>>(), h.Clock);
            return h;
        }
        public async Task<ManagedService> Seed()
        {
            var h = new SubscriptionManagementTests.Harness();
            await using var db = Factory.CreateDbContext();
            db.Games.Add(new() { Id = h.Managed.GameId, Slug = "project-zomboid", Name = "Project Zomboid" });
            db.HostingPlans.Add(h.Current);
            db.Orders.Add(new() { Id = h.Managed.OrderId, GameId = h.Managed.GameId, PlanId = h.Current.Id,
                GameSlug = "project-zomboid", PlanSlug = h.Current.Slug, PlanName = h.Current.Name,
                BillingPeriod = "monthly", Currency = "usd", Status = "paid", ProvisioningStage = "ready",
                CustomerEmail = "Owner@Example.Test", StripeCustomerId = "cus_test" });
            db.HostingServices.Add(new() { Id = h.Managed.Id, OrderId = h.Managed.OrderId, GameId = h.Managed.GameId,
                PlanId = h.Current.Id, StripeSubscriptionId = "sub_test", PterodactylServerId = 42,
                PterodactylServerUuid = "same-server-uuid", Status = "active" });
            await db.SaveChangesAsync();
            return h.Managed;
        }
        public async ValueTask DisposeAsync() { await Connection.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class TestFactory(DbContextOptions<CommerceDbContext> options) : IDbContextFactory<CommerceDbContext>
    {
        public CommerceDbContext CreateDbContext() => new(options);
    }
    private sealed class RecordingMail : ITrialMailTransport
    {
        public List<TrialMailMessage> Messages = [];
        public string Token => Regex.Match(Messages.Last().Body, "verify=([A-F0-9]{64})").Groups[1].Value;
        public Task DeliverAsync(TrialMailMessage message, CancellationToken ct) { Messages.Add(message); return Task.CompletedTask; }
    }
}
