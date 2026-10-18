using HowToSoftware.Hosting.Infrastructure.Stripe;
using HowToSoftware.Hosting.Models;
using Microsoft.Extensions.Options;

namespace HowToSoftware.Hosting.Services.Subscriptions;

public static class SubscriptionRegistration
{
    public static IServiceCollection AddCustomerArea(this IServiceCollection services, IConfiguration config, bool database)
    {
        services.AddOptions<CustomerAreaOptions>().Bind(config.GetSection("CustomerArea"))
            .Validate(o => !o.Enabled || database, "CustomerArea requires the commerce SQL Server database.").ValidateOnStart();
        services.AddOptions<SubscriptionManagementOptions>().Bind(config.GetSection("SubscriptionManagement"))
            .Validate(o => !o.Enabled || (database && o.ReconciliationEnabled && config.GetValue<bool>("CustomerArea:Enabled")),
                "Plan changes require SQL Server, CustomerArea and reconciliation.")
            .Validate(o => o.PollSeconds is >= 15 and <= 3600 && o.QuoteMinutes is >= 1 and <= 10,
                "Subscription poll/quote limits are invalid.").ValidateOnStart();
        services.AddOptions<SubscriptionManagementOptions>().Validate(o => !o.Enabled ||
            (config.GetSection(StripeOptions.SectionName).Get<StripeOptions>() is { IsConfigured: true, IsWebhookConfigured: true }
             && config.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>() is { IsConfigured: true }),
            "Paid plan changes require Stripe API/webhook and SMTP configuration.");
        if (database)
        {
            services.AddScoped<ICustomerAccess, CustomerAccessService>();
            services.AddScoped<ISubscriptionStore, SubscriptionStore>();
            services.AddScoped<ISubscriptionResources, PterodactylSubscriptionResources>();
            services.AddScoped<ISubscriptionManagement, SubscriptionManagementService>();
            services.AddScoped<ICustomerWorkspace, CustomerWorkspace>();
            services.AddHostedService<SubscriptionReconciliationWorker>();
        }
        else
        {
            services.AddSingleton<ICustomerAccess, UnavailableCustomerAccess>();
            services.AddSingleton<ICustomerWorkspace, UnavailableCustomerWorkspace>();
        }
        services.AddSingleton<ISubscriptionGateway>(p => p.GetRequiredService<StripeGateway>());
        return services;
    }
}

public sealed class SubscriptionReconciliationWorker(IServiceScopeFactory scopes,
    IOptions<SubscriptionManagementOptions> settings, IOptions<CustomerAreaOptions> customers, ILogger<SubscriptionReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.ReconciliationEnabled && !customers.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.Value.PollSeconds));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISubscriptionManagement>().ReconcileAsync(null, stoppingToken);
                if (customers.Value.Enabled) await scope.ServiceProvider.GetRequiredService<ICustomerWorkspace>().DeliverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Subscription worker will retry ({FailureClass}).", e.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
