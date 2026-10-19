using HowToSoftware.Hosting.Infrastructure.Stripe;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Services;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

public sealed class StorefrontCurrencyTests
{
    private static StorefrontCurrencyService Currency() => new(Options.Create(new HostingPlanPricingOptions()));

    [Theory]
    [InlineData("pt-BR,pt;q=0.9,en;q=0.8", "brl")]
    [InlineData("en-BR", "brl")]
    [InlineData("pt-PT,pt;q=0.9", "usd")]
    [InlineData("pt", "usd")]
    [InlineData("en-US,pt-BR;q=0.9", "usd")]
    [InlineData("en-US;q=0.5,pt-BR;q=0.9", "brl")]
    [InlineData("pt-BR;q=0,en-US;q=0.9", "usd")]
    [InlineData("pt-BR;q=broken,en-US", "usd")]
    [InlineData("pt-BR-arbitrary", "usd")]
    public void RawBrowserPreferenceDoesNotConfuseLanguageWithRegion(string header, string expected) =>
        Assert.Equal(expected, Currency().ResolveBrowserCurrency([header]));

    [Fact]
    public void MissingHeaderKeepsUsd() => Assert.Equal("usd", Currency().ResolveBrowserCurrency(null));

    [Theory]
    [InlineData(BillingPeriod.Monthly, 51.94)]
    [InlineData(BillingPeriod.Quarterly, 148.01)]
    [InlineData(BillingPeriod.Annual, 560.89)]
    public void ConvertsAuthoritativePeriodTotalOnce(BillingPeriod period, decimal expected)
    {
        var original = BillingPolicy.Quote(7.99m, period);
        var selected = Currency().ConvertQuote(original, "brl");
        Assert.Equal(expected, selected.FinalAmount);
        Assert.Equal(selected.BaseAmount, selected.FinalAmount + selected.DiscountAmount);
        Assert.Equal(51.94m, selected.MonthlyPrice);
        Assert.Equal(original, Currency().ConvertQuote(original, "usd"));
    }

    [Fact]
    public void FormatsAlreadyConvertedAmountsWithoutConvertingAgain()
    {
        Assert.Equal("R$ 51,94", Currency().FormatAmount(51.94m, "brl"));
        Assert.Equal("51,94", Currency().FormatNumber(51.94m, "brl"));
        Assert.Equal("R$ 51,94", Currency().FormatCatalogAmount(7.99m, "brl"));
        Assert.Equal("7.99", Currency().FormatCatalogNumber(7.99m, "usd"));
        Assert.Throws<ArgumentException>(() => Currency().ConvertAmount(7.99m, "eur"));
    }

    [Fact]
    public void PriceIdsCannotLeakBetweenCurrencies()
    {
        var options = new StripeOptions();
        options.PriceIds["plan:monthly"] = "price_legacy_usd";
        Assert.Equal("price_legacy_usd", options.FindPriceId("plan", "monthly"));
        Assert.Null(options.FindPriceId("plan", "monthly", "brl"));
        options.PriceIds["plan:monthly:brl"] = "price_brl";
        Assert.Equal("price_brl", options.FindPriceId("plan", "monthly", "BRL"));
        Assert.Null(options.FindPriceId("plan", "monthly", "eur"));
    }
}
