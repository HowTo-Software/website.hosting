# Project map and responsibilities

> **Status:** Checked against the local code; team review pending
>
> **Owner:** HTS / HowToSoftware maintainers
>
> **Last updated:** 2026-10-05

Use this map to find where behavior is implemented. [Architecture](phase-2-design/architecture.md) explains the relationships; the [index](README.md) organizes the guides.

## Structure

```text
src/HowToSoftware.Hosting/
  Components/        Razor pages, composition, and controls
  Models/            products, quotes, orders, and commerce entities
  Services/          application rules, content, purchases, and delivery
  Infrastructure/    configuration, SQL, security, Stripe, and Pterodactyl
  Data/              EF contexts, migrations, factories, and seed
  Endpoints/         payment HTTP contracts
  Localization/      localizer types, cultures, and resources
  wwwroot/           CSS, JS, images, fonts, and icons
tests/HowToSoftware.Hosting.Tests/
deploy/
.github/workflows/
docs/
```

Table paths are relative to `src/HowToSoftware.Hosting` unless indicated otherwise.

## Where to change behavior

| Need | Main files | Guide |
| --- | --- | --- |
| Service registration/pipeline | `Program.cs` | [Architecture](phase-2-design/architecture.md) |
| Customer area, email sessions, invoices, support, plan changes | `Components/Pages/CustomerAccount.razor`, `Endpoints/CustomerEndpoints.cs`, `Services/Subscriptions` | [Customer area](CUSTOMER-AREA.md) |
| Dormant AD identity boundary and Discord link/login | `Services/Identity`, `Endpoints/ExternalIdentityEndpoints.cs`, `Components/Account/AccountConnections.razor` | [External identities](EXTERNAL-IDENTITIES.md) |
| Routes and links | `Models/SiteRoutes.cs`, `Components/Pages` | [HTTP](phase-2-design/api-specification.md) |
| Available games | `Services/GameCatalog.cs` | [Scope](phase-1-inception/vision-and-scope.md) |
| Plan resources and recommendation | `Services/StaticPlanCatalogService.cs` | [Technical design](phase-2-design/technical-design.md) |
| Rates/overrides/custom limits | `Models/HostingPlanPricingOptions.cs`, `appsettings.json` | [Configuration](phase-3-development/configuration.md) |
| Periods and discounts | `Models/Billing.cs` (`BillingPolicy`) | [Technical design](phase-2-design/technical-design.md) |
| Purchase selection resolution | `Services/Payments/OrderPricingService.cs` | [Commerce](COMMERCE-ARCHITECTURE.md) |
| Checkout and promotions | `Services/Payments/StripeCheckoutService.cs`, `PromotionService.cs` | [Stripe](stripe-testing.md) |
| Signature and event receipt | `Endpoints/PaymentEndpoints.cs`, `Services/Payments/StripeWebhookHandler.cs` | [HTTP](phase-2-design/api-specification.md) |
| Queue, recovery, and installation | `Services/Orders/OrderFulfillment.cs` | [Runbook](phase-6-operations/runbook.md) |
| Panel clients, DTOs, and validation | `Infrastructure/Pterodactyl` | [Pterodactyl](PTERODACTYL-SETUP.md) |
| Creation service and lab guard | `Services/Provisioning` | [Pterodactyl](PTERODACTYL-SETUP.md) |
| Order state and transitions | `Models/Orders/Order.cs` | [Data](phase-2-design/data-model.md) |
| Commerce and migrations | `Data/CommerceDbContext.cs`, `Data/CommerceMigrations` | [SQL Server](SQLSERVER-SETUP.md) |
| Public layout | `Components/Layout/SiteHeader.razor`, `SiteFooter.razor` | [Frontend](FRONTEND-REDESIGN.md) |
| Centered homepage and large opening HTS wordmark | `Components/Pages/Home.razor`, `Components/Home/HeroExperience.razor` | [Frontend](FRONTEND-REDESIGN.md) |
| Plan comparison | `Components/Shop/PlanPicker.razor` | [Frontend](FRONTEND-REDESIGN.md) |
| Review and total | `Components/Pages/PlanReview.razor`, `Components/Shop/OrderSummary.razor` | [HTTP](phase-2-design/api-specification.md) |
| HTS wordmark | `Components/Shared/VectorWordmark.razor`, `wwwroot/js/vector-wordmark.js` | [ADR-0004](phase-2-design/adr/0004-preserve-blazor-dom.md) |
| Reveals and animated navigation | `MaskReveal.razor`, `RollingNavText.razor` in `Components/Shared`; `wwwroot/js/site.js` | [Frontend](FRONTEND-REDESIGN.md) |
| Palette and shared styles | `wwwroot/css/theme.css`, `app.css`, `storefront.css`, `no-script.css` | [Frontend](FRONTEND-REDESIGN.md) |
| Language | `Localization/SiteLocalization.cs`, `CultureEndpoints.cs`, `*.resx` | [Configuration](phase-3-development/configuration.md) |
| CSP, antiforgery, and rate limits | `Infrastructure/Security/SecurityHardening.cs` | [Hardening](SECURITY-HARDENING.md) |
| Deployment | `deploy/deploy.sh`, `deploy/docker-compose.production.yml` at repository root | [Deployment](DEPLOYMENT.md) |

## Text families

Regional prices and charge currency: `Services/StorefrontCurrencyService.cs`,
`Services/Payments/OrderPricingService.cs`, `Infrastructure/Stripe/StripeOptions.cs` and
`Components/Pages/PlanReview.razor`. See [regional pricing](REGIONAL-PRICING.md) for
browser detection, USD/BRL mappings, the index migration and dormant Pix configuration.

CommonText, HomeText, LoginText, HardwareText, CheckoutText, and StorefrontText have neutral EN resources and PT-BR counterparts. New visible feature copy belongs in the appropriate family and must preserve formatting placeholders in both languages.

## References and evidence

`docs/reference-sources` retains the collected public Skiper originals, which are not part of runtime. Supplied OriginKit code was adapted inside the application without publishing the attachment as a UI kit. `docs/frontend` contains screenshots and QA JSON, including historical versions identified in the guide.

The [test plan](phase-4-testing/test-plan.md) locates risk-based validation. Private configuration is excluded from this map; use setting names from the example, never real `.env` content.
