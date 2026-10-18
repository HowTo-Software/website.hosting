using HowToSoftware.Hosting.Components;
using HowToSoftware.Hosting.Data;
using HowToSoftware.Hosting.Endpoints;
using HowToSoftware.Hosting.Infrastructure.Configuration;
using HowToSoftware.Hosting.Infrastructure.Database;
using HowToSoftware.Hosting.Infrastructure.Pterodactyl;
using HowToSoftware.Hosting.Infrastructure.Security;
using HowToSoftware.Hosting.Infrastructure.Stripe;
using HowToSoftware.Hosting.Localization;
using HowToSoftware.Hosting.Models;
using HowToSoftware.Hosting.Services;
using HowToSoftware.Hosting.Services.Orders;
using HowToSoftware.Hosting.Services.Payments;
using HowToSoftware.Hosting.Services.Provisioning;
using HowToSoftware.Hosting.Services.Trials;
using HowToSoftware.Hosting.Services.Subscriptions;
using HowToSoftware.Hosting.Services.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

EnvironmentFile.LoadNearest();
EnvironmentFile.ApplyAspNetCoreAliases();
var builder = WebApplication.CreateBuilder(args);
var database = SqlServerOptions.FromConfiguration(builder.Configuration);

if (!string.IsNullOrWhiteSpace(database.ConnectionString)
    && !SqlServerOptions.IsPlaceholder(database.ConnectionString)
    && !database.IsDatabaseConfigured)
{
    // Never silently fall back to a different database because a production connection string
    // was malformed. The message intentionally does not echo the credential-bearing value.
    throw new InvalidOperationException(
        $"{SqlServerOptions.EnvironmentVariableName} is set but is not a valid SQL Server connection string naming a server and a database.");
}

// Blazor: the marketing pages render statically on the server for SEO and hydrate only the
// islands that genuinely need interactivity (navigation, FAQ, control-panel preview, sign-in).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Exception details must never cross a production Blazor circuit.
        options.DetailedErrors = builder.Environment.IsDevelopment();
        options.MaxBufferedUnacknowledgedRenderBatches = 10;
    });

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1024 * 1024;
    options.Limits.MaxRequestHeaderCount = 64;
    options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddSiteSecurity(builder.Environment, builder.Configuration);
builder.Services.AddDataProtection().SetApplicationName("HowToSoftware.Hosting");

// Localisation. Resources live beside their marker types under Localization/, so
// IStringLocalizer<CommonText> reads Localization/CommonText.resx and its .pt-BR sibling.
builder.Services.AddLocalization();
builder.Services.Configure<RequestLocalizationOptions>(SiteLocalization.Configure);

builder.Services.AddOptions<SiteOptions>()
    .Bind(builder.Configuration.GetSection(SiteOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<SiteOptions>,
    SiteOptionsValidator>();

// Plan prices are commercial values that change without the plans changing, so they arrive from
// configuration. A plan with no configured price renders as visibly unpriced rather than
// defaulting to a number nobody agreed to.
builder.Services.AddOptions<HostingPlanPricingOptions>()
    .Bind(builder.Configuration.GetSection(HostingPlanPricingOptions.SectionName));
builder.Services.AddOptions<MinecraftPlanOptions>()
    .Bind(builder.Configuration.GetSection(MinecraftPlanOptions.SectionName));
builder.Services.AddPublicPromotions(builder.Configuration);
builder.Services.AddTrialEmail(builder.Configuration);
builder.Services.AddServerTrials(builder.Configuration, database.IsDatabaseConfigured);

// ── Pterodactyl ───────────────────────────────────────────────────────────
//
// The API key is NOT in appsettings.json and must never be. It is read from the environment
// (Pterodactyl__ApiKey) or from user-secrets in development. Options are validated on first
// use rather than at startup, so the site still serves its marketing pages on a host that has
// no panel credentials - only provisioning needs them.
builder.Services.AddOptions<PterodactylOptions>()
    .Bind(builder.Configuration.GetSection(PterodactylOptions.SectionName));
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<PterodactylOptions>,
    PterodactylOptionsValidator>();

builder.Services.AddOptions<ProvisioningLabOptions>()
    .Bind(builder.Configuration.GetSection(ProvisioningLabOptions.SectionName));

// ── Stripe ────────────────────────────────────────────────────────────────
//
// Same rule as the panel key: the secret key and the webhook secret are NOT in appsettings.json.
// They come from the environment (Stripe__SecretKey, Stripe__WebhookSecret) or user-secrets.
// Without them every page still renders; only "continue to payment" is switched off, and it
// says so.
builder.Services.AddOptions<StripeOptions>()
    .Bind(builder.Configuration.GetSection(StripeOptions.SectionName));
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<StripeOptions>,
    StripeOptionsValidator>();

// ── Orders ────────────────────────────────────────────────────────────────
//
// SQL Server throughout, chosen by connection string. A factory rather than a scoped context,
// because the webhook and the fulfilment worker both need short units of work outside a
// component's scope. Registration never opens a connection, so the marketing pages still serve
// on a host that has no database credentials at all.
builder.Services.AddSingleton(database);
builder.Services.AddSingleton<SqlServerHealthCheck>();
builder.Services.AddHealthChecks().AddCheck<SqlServerHealthCheck>("sqlserver");

if (database.IsDatabaseConfigured)
{
    builder.Services.AddPooledDbContextFactory<CommerceDbContext>(options =>
        options.UseSqlServer(
            database.GetConnectionString(),
            sqlServer => sqlServer
                .MigrationsHistoryTable(CommerceDbContext.MigrationsHistoryTable)
                .EnableRetryOnFailure())
            .EnableSensitiveDataLogging(false));
    builder.Services.AddSingleton<IOrderStore, SqlServerOrderStore>();
    builder.Services.AddSingleton<IProvisioningStateStore, SqlServerProvisioningStateStore>();
    builder.Services.AddSingleton<IBillingStore, SqlServerBillingStore>();
    builder.Services.AddScoped<IPromotionService, PromotionService>();
    builder.Services.AddScoped<CommerceSeedService>();
}
else
{
    if (SqlServerOptions.TryBuildConnectionString(
            builder.Configuration.GetConnectionString(HostingDbContext.ConnectionName),
            out var hostingConnectionString))
    {
        builder.Services.AddDbContextFactory<HostingDbContext>(options =>
            options.UseSqlServer(
                hostingConnectionString,
                sqlServer => sqlServer
                    .MigrationsHistoryTable(HostingDbContext.MigrationsHistoryTable)
                    .EnableRetryOnFailure())
                .EnableSensitiveDataLogging(false));
        builder.Services.AddSingleton<IOrderStore, EfOrderStore>();
    }
    else
    {
        builder.Services.AddSingleton<IOrderStore, NullOrderStore>();
    }

    builder.Services.AddSingleton<IProvisioningStateStore, NullProvisioningStateStore>();
    builder.Services.AddSingleton<IBillingStore, NullBillingStore>();
    builder.Services.AddSingleton<IPromotionService, NullPromotionService>();
}

builder.Services.AddHttpClient<IPterodactylClient, PterodactylClient>(PterodactylClient.HttpClientName,
    (provider, client) =>
    {
        var options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<PterodactylOptions>>()
            .CurrentValue;

        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        // The Authorization header is attached per request inside the client, not here: it is
        // read from options each time so a rotated key takes effect without a restart, and it
        // stays in exactly one place in the codebase.
        client.DefaultRequestHeaders.Add("User-Agent", "HowToSoftware-Hosting/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Redirects are never legitimate on this API, and following one is actively dangerous:
        // the panel answers an unauthenticated request with a 302 to its HTML login page, so a
        // redirect-following client sees HTTP 200 and a page of HTML and calls it success.
        AllowAutoRedirect = false
    });

// Content and server telemetry are resolved through interfaces so the prototype's static and
// mock implementations can be replaced by CMS- and panel-backed services later on.
builder.Services.AddSingleton(TimeProvider.System);
// Marketing content is scoped rather than singleton now that its labels follow the request's
// culture; the data behind them is still compiled-in and allocation-cheap.
builder.Services.AddScoped<IMarketingContentService, StaticMarketingContentService>();
builder.Services.AddScoped<IPlanCatalogService, StaticPlanCatalogService>();
builder.Services.AddScoped<IGameCatalogService, StaticGameCatalogService>();
builder.Services.AddScoped<ICustomBuildService, RateCardBuildService>();
// The purchase flow. The browser sends a game, a plan and a period; everything with a
// currency sign on it is computed here, sent to Stripe from here, and checked here when
// Stripe's webhook reports it paid. Fulfilment runs off a queue so the webhook answers fast.
builder.Services.AddScoped<IOrderPricingService, OrderPricingService>();
builder.Services.AddSingleton<StripeGateway>();
builder.Services.AddSingleton<IStripeGateway>(p => p.GetRequiredService<StripeGateway>());
builder.Services.AddCustomerArea(builder.Configuration, database.IsDatabaseConfigured);
builder.Services.AddExternalIdentities(builder.Configuration, database.IsDatabaseConfigured);
builder.Services.AddSingleton<OrderFulfillmentQueue>();
builder.Services.AddScoped<IStripeCheckoutService, StripeCheckoutService>();
builder.Services.AddScoped<IStripeWebhookHandler, StripeWebhookHandler>();
builder.Services.AddScoped<IOrderFulfillmentService, OrderFulfillmentService>();
builder.Services.AddHostedService<OrderFulfillmentWorker>();
builder.Services.AddSingleton<IInfrastructureContentService, StaticInfrastructureContentService>();
builder.Services.AddSingleton<IHardwarePhotoLibrary, HardwarePhotoLibrary>();
builder.Services.AddSingleton<IZomboidPhotoLibrary, ZomboidPhotoLibrary>();
builder.Services.AddSingleton<IGameBannerLibrary, GameBannerLibrary>();
builder.Services.AddSingleton<IGameTemplateCatalog, GameTemplateCatalog>();
builder.Services.AddScoped<IServerPreviewService, MockServerPreviewService>();
builder.Services.AddScoped<IProvisioningService, ProvisioningService>();
builder.Services.AddSingleton<IProvisioningLabGuard, ProvisioningLabGuard>();
// No identity provider exists yet. This gateway authenticates nobody and stores nothing; see
// PrototypeAuthenticationGateway for why that is the only honest stand-in.
builder.Services.AddScoped<IAuthenticationGateway, PrototypeAuthenticationGateway>();

var app = builder.Build();

if (args.Contains("--migrate-commerce", StringComparer.Ordinal))
{
    if (!database.IsDatabaseConfigured)
    {
        Console.Error.WriteLine(
            $"SQL Server not configured. Set {SqlServerOptions.EnvironmentVariableName} before applying commerce migrations.");
        // A migration that applied nothing must not report success, or a deployment job will
        // carry on and start the application against a schema that does not exist.
        Environment.ExitCode = 1;
        return;
    }

    await using var scope = app.Services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CommerceDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    if (args.Contains("--seed-commerce", StringComparer.Ordinal))
    {
        await scope.ServiceProvider.GetRequiredService<CommerceSeedService>().SeedAsync();
    }

    Console.WriteLine($"Commerce database is up to date: {database.DescribeTarget()}");
    return;
}

// Every database is now remote, so nothing is migrated on startup: a deploy must never be able
// to reshape a shared server just by restarting a process. Both schemas move under an explicit
// flag instead.
if (args.Contains("--migrate-hosting", StringComparer.Ordinal))
{
    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(HostingDbContext.ConnectionName)))
    {
        Console.Error.WriteLine(
            $"Set ConnectionStrings__{HostingDbContext.ConnectionName} before applying hosting migrations.");
        Environment.ExitCode = 1;
        return;
    }

    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HostingDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.Migrate();
    Console.WriteLine("Hosting database is up to date.");
    return;
}

// Forwarded headers are accepted only from framework defaults (loopback) or IPs explicitly
// listed under Security:KnownProxies. This must precede HTTPS and IP-based rate limiting.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseSiteSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    // Local development intentionally runs on http://localhost. Production never does.
    app.UseHttpsRedirection();
}

// Must run before the components: it sets the culture for the request that renders the page
// and, just as importantly, for the request that opens an interactive circuit, so both halves
// of a page speak the same language.
app.UseRequestLocalization();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapCultureSelection();
app.MapPaymentEndpoints();
app.MapTrialEndpoints();
app.MapCustomerEndpoints();
app.MapExternalIdentityEndpoints();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    AllowCachingResponses = false,
    ResponseWriter = static async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status is HealthStatus.Unhealthy ? "Unhealthy" : "Healthy"
        });
    }
}).RequireRateLimiting(SecurityRateLimitPolicies.Health)
    .WithName("SiteHealth");

// The Project Zomboid page moved under /game-hosting. Links already out in the world keep
// working, and search engines are told the move is permanent.
app.MapGet(SiteRoutes.LegacyProjectZomboid, () => Results.Redirect(SiteRoutes.ProjectZomboid, permanent: true));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireRateLimiting(SecurityRateLimitPolicies.PublicPages);

app.Run();

/// <summary>
/// Makes the entry point visible to the integration tests, which host the whole application
/// in-process to exercise the webhook endpoint end to end.
/// </summary>
public partial class Program
{
}

// =============================================================
// © 2026 Henry Lawrence Cahill (HowToSoftware). All rights reserved.
// Contact: henry.cahill@howtoosoftware.com | https://howtoosoftware.com
// =============================================================
