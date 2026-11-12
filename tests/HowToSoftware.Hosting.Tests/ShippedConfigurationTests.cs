using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Tests;

/// <summary>
/// Binds the <c>appsettings.json</c> the site actually ships and checks what comes out.
/// </summary>
/// <remarks>
/// The other pricing tests construct options in code, which proves the arithmetic but not that
/// the file feeding it is shaped the way the binder expects. A renamed section or a rate nested
/// one level too deep compiles, passes every other test, and ships a page reading PRICE PENDING.
/// </remarks>
public class ShippedConfigurationTests : IDisposable
{
    private readonly CultureScope _culture = new(SupportedCultures.Default);

    public void Dispose() => _culture.Dispose();

    private static HostingPlanPricingOptions ShippedPricing()
    {
        var options = new HostingPlanPricingOptions();

        new ConfigurationBuilder()
            .AddJsonFile(LocateAppSettings(), optional: false)
            .Build()
            .GetSection(HostingPlanPricingOptions.SectionName)
            .Bind(options);

        return options;
    }

    [Fact]
    public void TheShippedRateCardBinds()
    {
        var pricing = ShippedPricing();

        Assert.True(
            pricing.Rates.IsConfigured,
            "HostingPlans:Rates in appsettings.json did not bind to a usable rate card.");
        Assert.False(string.IsNullOrWhiteSpace(pricing.CurrencySymbol));
    }

    [Fact]
    public void EveryShippedPlanIsPriced()
    {
        var catalog = new StaticPlanCatalogService(
            TestLocalizer.For<HomeText>(),
            Options.Create(ShippedPricing()));

        Assert.True(catalog.HasPricing);
        Assert.All(catalog.Plans, plan =>
            Assert.True(plan.IsPriced, $"{plan.Slug} renders as unpriced with the shipped configuration."));
    }

    /// <summary>
    /// Every tier needs an entry - blank, but present - so the file itself lists where a
    /// hand-set price would go. A slug missing here is a slug nobody can override.
    /// </summary>
    [Fact]
    public void EveryPlanSlugHasAnOverrideSlot()
    {
        var pricing = ShippedPricing();
        var catalog = new StaticPlanCatalogService(
            TestLocalizer.For<HomeText>(), Options.Create(pricing));

        Assert.All(catalog.Plans, plan =>
            Assert.True(
                pricing.Prices.ContainsKey(plan.Slug),
                $"appsettings.json has no HostingPlans:Prices entry for {plan.Slug}."));
    }

    /// <summary>The API key must never appear in a file that is committed.</summary>
    [Fact]
    public void TheShippedConfigurationCarriesNoApiKey()
    {
        var raw = File.ReadAllText(LocateAppSettings());

        Assert.DoesNotContain("ptla_", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Stripe secret key and webhook secret arrive from the environment or user-secrets. The
    /// committed file must not even have the property names, so nobody is invited to fill them in.
    /// </summary>
    [Fact]
    public void TheShippedConfigurationCarriesNoStripeSecret()
    {
        var raw = File.ReadAllText(LocateAppSettings());

        Assert.DoesNotContain("sk_live", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk_test", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whsec_", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebhookSecret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheShippedCurrencyIsUsableByStripe()
    {
        var pricing = ShippedPricing();

        Assert.Equal(3, pricing.CurrencyCode.Length);
        Assert.Equal(pricing.CurrencyCode.ToLowerInvariant(), pricing.CurrencyCode);
    }

    [Fact]
    public void TheEnvironmentTemplateCarriesOnlyPlaceholders()
    {
        var template = Path.Combine(Path.GetDirectoryName(LocateAppSettings())!, "..", "..", ".env.example");

        Assert.True(File.Exists(template), ".env.example is missing from the repository root.");

        foreach (var line in File.ReadAllLines(template))
        {
            if (line.StartsWith('#') || !line.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[(line.IndexOf('=') + 1)..];

            if (value.StartsWith("sk_", StringComparison.Ordinal)
                || value.StartsWith("pk_", StringComparison.Ordinal)
                || value.StartsWith("whsec_", StringComparison.Ordinal)
                || value.StartsWith("ptla_", StringComparison.Ordinal))
            {
                Assert.True(
                    value.Contains("REPLACE_ME", StringComparison.Ordinal)
                    || value.Contains("API_AQUI", StringComparison.Ordinal),
                    $"A credential-shaped value in .env.example is not a recognised placeholder: {line[..line.IndexOf('=')]}.");
            }
        }
    }

    /// <summary>
    /// The template is part of the deployment contract. Keep every credential and topology
    /// setting discoverable there instead of making an operator read source code to find it.
    /// </summary>
    [Fact]
    public void TheEnvironmentTemplateDocumentsEveryExternalIntegration()
    {
        var template = Path.Combine(Path.GetDirectoryName(LocateAppSettings())!, "..", "..", ".env.example");
        var raw = File.ReadAllText(template);

        var requiredNames = new[]
        {
            "STRIPE_PUBLISHABLE_KEY",
            "STRIPE_SECRET_KEY",
            "STRIPE_WEBHOOK_SECRET",
            "STRIPE_SUCCESS_URL",
            "STRIPE_CANCEL_URL",
            "STRIPE_CURRENCY",
            "STRIPE_WEBHOOK_TOLERANCE_SECONDS",
            "SQLSERVER_CONNECTION_STRING",
            "PTERODACTYL_PANEL_URL",
            "PTERODACTYL_APPLICATION_API_KEY",
            "PTERODACTYL_LOCATION_ID",
            "PTERODACTYL_NEST_ID",
            "PTERODACTYL_EGG_ID",
            "PTERODACTYL_TIMEOUT_SECONDS",
            "PTERODACTYL_DEPLOY_TESTS_ENABLED",
            "APP_BASE_URL",
            "APP_ENVIRONMENT",
            "ConnectionStrings__Hosting",
            "Authentik__BaseUrl",
            "Authentik__ApiToken"
        };

        Assert.All(requiredNames, name => Assert.Contains(name, raw, StringComparison.Ordinal));
    }

    private static string LocateAppSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "HowToSoftware.Hosting", "appsettings.json");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find appsettings.json above {AppContext.BaseDirectory}.");
    }
}

// =============================================================
// (c) 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
