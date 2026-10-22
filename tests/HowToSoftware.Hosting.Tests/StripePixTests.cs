using HowToSoftware.Hosting.Infrastructure.Stripe;
using Microsoft.Extensions.Configuration;

namespace HowToSoftware.Hosting.Tests;

public sealed class StripePixTests
{
    [Fact]
    public void ShippedPixConfigurationUsesConfirmedUsAccountAndMerchantAbsorbedIofButStaysDisabled()
    {
        var options = ShippedOptions();

        Assert.Equal("US", options.PixSubscriptions.AccountCountry);
        Assert.Equal("always", options.PixSubscriptions.AmountIncludesIof);
        Assert.False(options.PixSubscriptions.Enabled);
        Assert.True(new StripeOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void OperatorCanEnablePixWithoutReplacingTheConfirmedShippedCountryAndIofPolicy()
    {
        var options = ShippedOptions(new Dictionary<string, string?>
        {
            ["Stripe:PixSubscriptions:Enabled"] = "true"
        });

        Assert.True(options.PixSubscriptions.Enabled);
        Assert.Equal("US", options.PixSubscriptions.AccountCountry);
        Assert.Equal("always", options.PixSubscriptions.AmountIncludesIof);
        Assert.True(new StripeOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void PixIsOptInWithoutImplicitCountryOrTaxPolicy()
    {
        var options = new StripeOptions();
        Assert.False(options.PixSubscriptions.Enabled);
        Assert.Empty(options.PixSubscriptions.AccountCountry);
        Assert.Empty(options.PixSubscriptions.AmountIncludesIof);
        Assert.True(new StripeOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("BR", "always", false)]
    [InlineData("ZZ", "always", false)]
    [InlineData("US", "", false)]
    [InlineData("US", "sometimes", false)]
    [InlineData("US", "always", true)]
    [InlineData("GB", "never", true)]
    [InlineData("PT", "always", true)]
    public void EnablingRequiresSupportedForeignAccountAndExplicitIofPolicy(string country, string iof, bool valid)
    {
        var options = new StripeOptions { PixSubscriptions = new() { Enabled = true, AccountCountry = country, AmountIncludesIof = iof } };
        Assert.Equal(valid, new StripeOptionsValidator().Validate(null, options).Succeeded);
    }

    private static StripeOptions ShippedOptions(Dictionary<string, string?>? overrides = null)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Join(directory.FullName, "src", "HowToSoftware.Hosting", "appsettings.json");
            if (File.Exists(path))
            {
                // Deliberately bind only committed public JSON and explicit test overrides:
                // no environment variables, user secrets, dotenv, or external services.
                using var configuration = new ConfigurationManager();
                configuration.AddJsonFile(path, optional: false);
                if (overrides is not null) configuration.AddInMemoryCollection(overrides);
                var options = new StripeOptions();
                configuration.GetSection(StripeOptions.SectionName).Bind(options);
                return options;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not find appsettings.json above {AppContext.BaseDirectory}.");
    }
}
