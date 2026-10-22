using System.Data.Common;
using HowToSoftware.Hosting.Models;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Payments;

/// <summary>Only customer-facing information, with no private promotion metadata.</summary>
public sealed record PublicPromotionOffer(
    string Code,
    PricedPlan Priced,
    decimal DiscountAmount,
    decimal FinalAmount,
    string ReviewHref);

public interface IPublicPromotionService
{
    Task<IReadOnlyList<PublicPromotionOffer>> GetOffersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicPromotionOffer>> GetOffersAsync(string currency, CancellationToken cancellationToken = default);
}

/// <summary>
/// Advertises only explicitly opted-in codes that pass the real checkout eligibility rules
/// for an existing, priced plan. No promotion table listing, writes or redemption happen here.
/// </summary>
public sealed class PublicPromotionService(
    IOptions<PublicPromotionOptions> options,
    IOrderPricingService pricing,
    IPromotionService promotions,
    ILogger<PublicPromotionService> logger) : IPublicPromotionService
{
    public Task<IReadOnlyList<PublicPromotionOffer>> GetOffersAsync(CancellationToken cancellationToken = default) =>
        GetOffersAsync("usd", cancellationToken);

    public async Task<IReadOnlyList<PublicPromotionOffer>> GetOffersAsync(string currency, CancellationToken cancellationToken = default)
    {
        if (options.Value.Campaigns.Count == 0)
        {
            return [];
        }

        var offers = new List<PublicPromotionOffer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            foreach (var campaign in options.Value.Campaigns.Take(PublicPromotionOptions.MaximumCampaigns))
            {
                var code = campaign.Code.Trim().ToUpperInvariant();

                if (code.Length is 0 or > 64
                    || !BillingPolicy.TryParse(campaign.Period, out var period)
                    || pricing.Price(campaign.GameSlug, campaign.PlanSlug, period, currency) is not { } priced)
                {
                    continue;
                }

                var key = $"{code}:{priced.Game.Slug}:{priced.Plan.Slug}:{period}";

                if (!seen.Add(key))
                {
                    continue;
                }

                var result = await promotions.ValidateAsync(
                    code, priced.Game.Slug, priced.Plan.Slug, priced.Quote, budget.Token, priced.CurrencyCode);

                if (result is null || result.DiscountAmount <= 0m || result.FinalAmount < 0m)
                {
                    continue;
                }

                offers.Add(new PublicPromotionOffer(
                    code,
                    priced,
                    result.DiscountAmount,
                    result.FinalAmount,
                    $"{SiteRoutes.Review(priced.Game.Slug, priced.Plan.Slug, period)}&currency={priced.CurrencyCode}&promo={Uri.EscapeDataString(code)}"));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Offers are optional content. A slow database must not prevent the storefront
            // from rendering. Do not log connection strings, coupon codes or exception text.
            logger.LogWarning("Public promotion validation exceeded its time budget; announcements were omitted.");
            return [];
        }
        catch (Exception exception) when (exception is DbException or TimeoutException
            || exception.InnerException is DbException)
        {
            logger.LogWarning("Public promotion announcements were omitted after a database availability failure ({FailureClass}).",
                exception.GetType().Name);
            return [];
        }

        return offers;
    }
}

public static class PublicPromotionRegistration
{
    public static IServiceCollection AddPublicPromotions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PublicPromotionOptions>()
            .Bind(configuration.GetSection(PublicPromotionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PublicPromotionOptions>, PublicPromotionOptionsValidator>();
        services.AddScoped<IPublicPromotionService, PublicPromotionService>();
        return services;
    }
}
