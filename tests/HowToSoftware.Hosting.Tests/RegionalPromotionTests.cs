using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Models.Commerce;
using HowToSoftware.Hosting.Services;
using HowToSoftware.Hosting.Services.Payments;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

public sealed class RegionalPromotionTests
{
    [Theory]
    [InlineData("usd", "fixed", null, false)]
    [InlineData(null, "fixed", null, false)]
    [InlineData(null, "percent", 100L, false)]
    [InlineData(null, "percent", null, true)]
    [InlineData("brl", "fixed", null, true)]
    [InlineData("brl", "percent", null, true)]
    public async Task PromotionAmountsAndThresholdsHaveExplicitCurrencySemantics(
        string? couponCurrency, string type, long? minimum, bool valid)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
        var factory = new Factory(new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(connection).Options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript()
                .Replace("nvarchar(max)", "TEXT", StringComparison.OrdinalIgnoreCase));
            db.PromotionCodes.Add(new PromotionCode { Id = Guid.NewGuid(), Code = "REGIONAL", Active = true,
                Currency = couponCurrency, DiscountType = type, FixedAmountCents = type == "fixed" ? 200 : null,
                DiscountPercent = type == "percent" ? 10 : null, MinimumAmountCents = minimum, Stackable = true });
            await db.SaveChangesAsync();
        }
        var quote = new StorefrontCurrencyService(Options.Create(new HostingPlanPricingOptions()))
            .ConvertQuote(BillingPolicy.Quote(7.99m, BillingPeriod.Monthly), "brl");
        var result = await new PromotionService(factory).ValidateAsync("REGIONAL", "project-zomboid", "zomboid-4gb", quote, default, "brl");
        Assert.Equal(valid, result is not null);
        if (result is not null)
            Assert.Equal(type == "fixed" ? 49.94m : 46.75m, result.FinalAmount);
        await using var read = factory.CreateDbContext();
        Assert.Empty(await read.PromotionRedemptions.ToListAsync()); // Eligibility is read-only.
    }

    private sealed class Factory(DbContextOptions<CommerceDbContext> options) : IDbContextFactory<CommerceDbContext>
    {
        public CommerceDbContext CreateDbContext() => new(options);
    }
}
