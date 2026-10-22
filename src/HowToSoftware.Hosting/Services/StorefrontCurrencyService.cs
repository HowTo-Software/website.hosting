using System.Globalization;
using System.Text.RegularExpressions;
using HowToSoftware.Hosting.Models;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services;

/// <summary>Server-owned USD/BRL conversion and presentation, independent of UI language.</summary>
public sealed class StorefrontCurrencyService(IOptions<HostingPlanPricingOptions> options)
{
    public static bool TryNormalize(string? value, out string currency)
    {
        currency = (value ?? "usd").Trim().ToLowerInvariant();
        return currency is "usd" or "brl";
    }

    public string ResolveBrowserCurrency(IEnumerable<string?>? acceptLanguages)
    {
        var candidates = new List<(string Tag, decimal Quality, int Position)>();
        var position = 0;
        foreach (var header in acceptLanguages ?? [])
            foreach (var parts in (header ?? "").Split(',').Select(raw => raw.Trim().Split(';')))
            {
                var quality = 1m;
                foreach (var pair in parts.Skip(1).Select(parameter => parameter.Trim().Split('=', 2))
                    .Where(pair => pair.Length == 2 && pair[0].Equals("q", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!decimal.TryParse(pair[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality)
                        || quality is < 0m or > 1m) quality = 0m;
                }
                if (quality > 0m) candidates.Add((parts[0].Trim(), quality, position));
                position++;
            }
        // Language alone is not geography: Portuguese (Portugal) and bare "pt" stay USD.
        var preferred = candidates.OrderByDescending(x => x.Quality).ThenBy(x => x.Position).FirstOrDefault().Tag;
        return preferred is not null && Regex.IsMatch(preferred, "^[a-z]{2,3}(?:-[a-z]{4})?-BR$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? "brl" : "usd";
    }

    public string Symbol(string currency) => Normalize(currency) == "brl" ? "R$" : "$";
    public decimal ConvertAmount(decimal baseAmount, string currency)
    {
        var selected = Normalize(currency);
        if (!string.Equals(options.Value.CurrencyCode, "usd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Regional pricing requires a USD catalogue.");
        if (selected == "usd") return BillingPolicy.RoundToCent(baseAmount);
        if (!decimal.TryParse(options.Value.RegionalPricing.BrlPerUsd, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var rate) || rate <= 0m || rate > 1000m)
            throw new InvalidOperationException("The configured BRL rate is invalid.");
        return BillingPolicy.RoundToCent(checked(baseAmount * rate));
    }

    public BillingQuote ConvertQuote(BillingQuote quote, string currency)
    {
        var baseAmount = ConvertAmount(quote.BaseAmount, currency);
        var finalAmount = ConvertAmount(quote.FinalAmount, currency);
        return quote with
        {
            MonthlyPrice = ConvertAmount(quote.MonthlyPrice, currency),
            BaseAmount = baseAmount,
            FinalAmount = finalAmount,
            DiscountAmount = baseAmount - finalAmount
        };
    }

    public string FormatNumber(decimal selectedCurrencyAmount, string currency) =>
        selectedCurrencyAmount.ToString("N2", CultureInfo.GetCultureInfo(Normalize(currency) == "brl" ? "pt-BR" : "en-US"));
    public string FormatAmount(decimal selectedCurrencyAmount, string currency) => $"{Symbol(currency)} {FormatNumber(selectedCurrencyAmount, currency)}";
    public string FormatCatalogNumber(decimal baseAmount, string currency) => FormatNumber(ConvertAmount(baseAmount, currency), currency);
    public string FormatCatalogAmount(decimal baseAmount, string currency) => $"{Symbol(currency)} {FormatCatalogNumber(baseAmount, currency)}";

    private static string Normalize(string currency) => TryNormalize(currency, out var normalized)
        ? normalized : throw new ArgumentException("Unsupported currency.", nameof(currency));
}
