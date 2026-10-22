using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

public sealed class RegionalCurrencyPersistenceTests
{
    [Fact]
    public async Task SamePlanAndPeriodCanPersistBothUsdAndBrl()
    {
        await using var harness = await Harness.CreateAsync();
        await using var db = harness.Factory.CreateDbContext();
        var planId = await AddPlanAsync(db);
        db.PlanBillingPrices.AddRange(Price(planId, "USD", 799), Price(planId, "BRL", 5194));
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var prices = await db.PlanBillingPrices.Where(x => x.PlanId == planId).ToListAsync();
        Assert.Equal(2, prices.Count);
        Assert.Equal(799, Assert.Single(prices, x => x.Currency == "USD").AmountCents);
        Assert.Equal(5194, Assert.Single(prices, x => x.Currency == "BRL").AmountCents);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("BRL")]
    public async Task DuplicatePlanPeriodAndCurrencyIsRejected(string currency)
    {
        await using var harness = await Harness.CreateAsync();
        await using var db = harness.Factory.CreateDbContext();
        var planId = await AddPlanAsync(db);
        db.PlanBillingPrices.Add(Price(planId, currency, 799));
        await db.SaveChangesAsync();
        db.PlanBillingPrices.Add(Price(planId, currency, 999));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        db.ChangeTracker.Clear();
        Assert.Equal(799, (await db.PlanBillingPrices.SingleAsync()).AmountCents);
    }

    [Fact]
    public async Task DevelopmentSeedAddsRegionalPricesAndPreservesCurrencySpecificStripeMappingsOnReruns()
    {
        await using var harness = await Harness.CreateAsync();
        var pricing = Options.Create(new HostingPlanPricingOptions
        {
            Rates = new PlanRateCard { CpuPer100Percent = "0.90", MemoryPerGb = "1.20", DiskPerBlock = "0.30" },
            Prices = new() { ["zomboid-4gb"] = "7.99" },
            RegionalPricing = new() { BrlPerUsd = "6.50" }
        });
        var catalog = new StaticGameCatalogService(TestLocalizer.For<CheckoutText>(),
            new StaticPlanCatalogService(TestLocalizer.For<HomeText>(), pricing));
        var seed = new CommerceSeedService(harness.Factory, catalog, pricing,
            new StaticOptionsMonitor<PterodactylOptions>(new()), new DevelopmentEnvironment(),
            new StorefrontCurrencyService(pricing));
        await seed.SeedAsync();

        // Emulate a previously seeded USD-only database with dashboard-managed mappings.
        Dictionary<Guid, (string? Product, string? Price)> originalUsd;
        await using (var db = harness.Factory.CreateDbContext())
        {
            db.PlanBillingPrices.RemoveRange(await db.PlanBillingPrices.Where(x => x.Currency == "BRL").ToListAsync());
            var prices = await db.PlanBillingPrices.Where(x => x.Currency == "USD").ToListAsync();
            foreach (var price in prices)
            {
                price.StripeProductId = $"prod_usd_{price.Id:N}";
                price.StripePriceId = $"price_usd_{price.Id:N}";
            }
            await db.SaveChangesAsync();
            originalUsd = prices.ToDictionary(x => x.Id, x => (x.StripeProductId, x.StripePriceId));
        }

        await seed.SeedAsync();
        Dictionary<Guid, (string? Product, string? Price)> allMappings;
        await using (var db = harness.Factory.CreateDbContext())
        {
            var plans = await db.HostingPlans.ToListAsync();
            var prices = await db.PlanBillingPrices.ToListAsync();
            Assert.NotEmpty(plans);
            Assert.Equal(plans.Count * 6, prices.Count);
            foreach (var planPrices in plans.Select(plan => prices.Where(x => x.PlanId == plan.Id).ToArray()))
            {
                Assert.Equal(6, planPrices.Length);
                foreach (var period in new[] { "monthly", "quarterly", "annual" })
                    foreach (var currency in new[] { "USD", "BRL" })
                        Assert.Single(planPrices, x => x.BillingPeriod == period && x.Currency == currency);
            }
            var firstPlan = Assert.Single(plans, x => x.Slug == "zomboid-4gb");
            foreach (var (period, usd, brl) in new[]
                     { ("monthly", 799L, 5194L), ("quarterly", 2277L, 14801L), ("annual", 8629L, 56089L) })
            {
                Assert.Equal(usd, Assert.Single(prices, x => x.PlanId == firstPlan.Id && x.BillingPeriod == period && x.Currency == "USD").AmountCents);
                Assert.Equal(brl, Assert.Single(prices, x => x.PlanId == firstPlan.Id && x.BillingPeriod == period && x.Currency == "BRL").AmountCents);
            }
            foreach (var price in prices.Where(x => x.Currency == "USD"))
                Assert.Equal(originalUsd[price.Id], (price.StripeProductId, price.StripePriceId));
            foreach (var price in prices.Where(x => x.Currency == "BRL"))
            {
                Assert.Null(price.StripeProductId);
                Assert.Null(price.StripePriceId);
                price.StripeProductId = $"prod_brl_{price.Id:N}";
                price.StripePriceId = $"price_brl_{price.Id:N}";
            }
            await db.SaveChangesAsync();
            allMappings = prices.ToDictionary(x => x.Id, x => (x.StripeProductId, x.StripePriceId));
        }

        await seed.SeedAsync();
        await using var read = harness.Factory.CreateDbContext();
        var reseeded = await read.PlanBillingPrices.ToListAsync();
        Assert.Equal(allMappings.Count, reseeded.Count);
        foreach (var price in reseeded)
            Assert.Equal(allMappings[price.Id], (price.StripeProductId, price.StripePriceId));
        Assert.Single(await read.GameDeploymentProfiles.ToListAsync());
    }

    private static PlanBillingPrice Price(Guid planId, string currency, long amount) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = planId,
        BillingPeriod = "monthly",
        Currency = currency,
        AmountCents = amount,
        Active = true
    };

    private static async Task<Guid> AddPlanAsync(CommerceDbContext db)
    {
        var gameId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        db.Games.Add(new CommerceGame { Id = gameId, Slug = "fixture-game", Name = "Fixture game" });
        db.HostingPlans.Add(new CommerceHostingPlan
        {
            Id = planId,
            GameId = gameId,
            Slug = "fixture-plan",
            Name = "Fixture plan",
            Currency = "USD",
            RamMb = 4096,
            CpuPercent = 300,
            DiskMb = 25600,
            MonthlyPriceCents = 799
        });
        await db.SaveChangesAsync();
        return planId;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public Factory Factory { get; private set; } = null!;

        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();
            await harness._connection.OpenAsync();
            harness._connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            harness.Factory = new(new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(harness._connection).Options);
            await using var db = harness.Factory.CreateDbContext();
            // Production uses SQL Server; SQLite executes the same relational model locally.
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript()
                .Replace("nvarchar(max)", "TEXT", StringComparison.OrdinalIgnoreCase));
            return harness;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<CommerceDbContext> options) : IDbContextFactory<CommerceDbContext>
    {
        public CommerceDbContext CreateDbContext() => new(options);
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "RegionalCurrencyPersistenceTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
