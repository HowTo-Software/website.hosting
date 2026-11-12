# Configuration and environment variables

> **Status:** Checked against the local code; team review pending
>
> **Owner:** HTS / HowToSoftware maintainers
>
> **Last updated:** 2026-10-05

## Loading and precedence

`EnvironmentFile.LoadNearest` finds the first `.env` in the current directory or its ancestors. It does not replace process variables already set. `HTS_SKIP_DOTENV=true` disables discovery.

Portable aliases are then mapped to ASP.NET names using `__`. An existing ASP.NET name takes precedence over its alias. Standard providers load appsettings, environment-specific appsettings, Development user-secrets, environment variables, and arguments; arguments override earlier providers.

Commerce SQL has its own additional selection order: `SQLSERVER_CONNECTION_STRING`, then `SqlServer:ConnectionString`, then `ConnectionStrings:Commerce`. Changing only an alternative section may therefore not replace an already populated commerce alias.

Nonsecret defaults are in [appsettings.json](../../src/HowToSoftware.Hosting/appsettings.json); integration formats are in [.env.example](../../.env.example). Never put secrets in versioned JSON.

## Site and security

| Alias / native name | Purpose |
| --- | --- |
| APP_BASE_URL / Site__BaseUrl | Public origin, canonical URL, and checkout return |
| APP_ALLOWED_HOSTS / AllowedHosts | Semicolon-separated allowed hosts |
| APP_ENVIRONMENT / ASPNETCORE_ENVIRONMENT | ASP.NET Core environment |
| Site__Name | Public name |
| Site__ContactEmail | Contact/estimate recipient |
| Security__KnownProxies__0, __1… | Proxy IPs authorized to send forwarded headers |
| HTS_SKIP_DOTENV | Disables .env discovery |

**Check the domain before deployment:** the workflow Environment points to `host.howto.software`, while BaseUrl/AllowedHosts defaults include `howtoosoftware.com` and `www.howtoosoftware.com`. These files do not prove the effective production domain. Align configuration with the Host forwarded by the proxy.

## Database

| Name | Purpose |
| --- | --- |
| SQLSERVER_CONNECTION_STRING | Preferred commerce connection |
| SqlServer__ConnectionString | Alternative commerce section |
| ConnectionStrings__Commerce | Another commerce alternative |
| ConnectionStrings__Hosting | Smaller orders context when commerce is unconfigured |

A populated invalid commerce connection string fails startup rather than silently selecting another database. Runtime and migration secrets should use different identities with minimal permissions. Procedure: [SQL Server](../SQLSERVER-SETUP.md).

## Stripe

| Alias / native name | Purpose |
| --- | --- |
| STRIPE_SECRET_KEY / Stripe__SecretKey | Backend key, test or live according to environment |
| STRIPE_WEBHOOK_SECRET / Stripe__WebhookSecret | Webhook endpoint validation |
| STRIPE_PUBLISHABLE_KEY / Stripe__PublishableKey | Available setting; current hosted Checkout does not require Stripe.js |
| STRIPE_SUCCESS_URL / Stripe__SuccessUrl | Return URL with literal {CHECKOUT_SESSION_ID} token |
| STRIPE_CANCEL_URL / Stripe__CancelUrl | Cancellation return; accepts example tokens |
| STRIPE_WEBHOOK_TOLERANCE_SECONDS / Stripe__WebhookToleranceSeconds | Signature tolerance window |
| Stripe__PriceIds__zomboid-4gb:monthly | Example recurring price per plan/period |
| STRIPE_CURRENCY / HostingPlans__CurrencyCode | Quote and payment currency |

Without pre-created PriceIds, the service builds an inline price. Registered Stripe prices must match the snapshot accepted by the backend. Do not mix live keys with test webhooks or mismatched catalog values. [Stripe procedure](../stripe-testing.md).

## Pterodactyl

| Alias / section | Purpose |
| --- | --- |
| PTERODACTYL_PANEL_URL / Pterodactyl__BaseUrl | HTTPS panel root, without /api/application suffix |
| PTERODACTYL_APPLICATION_API_KEY / Pterodactyl__ApiKey | Application API key; backend only |
| PTERODACTYL_LOCATION_ID / Pterodactyl__LocationId | Target location |
| PTERODACTYL_NEST_ID / Pterodactyl__NestId | Game nest |
| PTERODACTYL_EGG_ID / Pterodactyl__EggId | Installation egg |
| PTERODACTYL_DOCKER_IMAGE / Pterodactyl__DockerImage | Server image |
| PTERODACTYL_STARTUP_COMMAND / Pterodactyl__StartupCommand | Startup command |
| PTERODACTYL_TIMEOUT_SECONDS / Pterodactyl__TimeoutSeconds | HTTP timeout |
| Pterodactyl__PortRange__0… | Eligible allocations according to configuration |
| Pterodactyl__Environment__NAME | Egg-required variables |
| PTERODACTYL_DEPLOY_TESTS_ENABLED / ProvisioningTest__Enabled | Lab; works only with Development |

A `ptlc_` key is Client API and unsuitable for provisioning; use Application API `ptla_`. Options are validated when used: public pages can render without the panel. [Pterodactyl procedure](../PTERODACTYL-SETUP.md).

## Prices and estimates

`HostingPlans__CurrencySymbol` and `CurrencyCode` must describe the same currency; language selection does not convert it. `Rates__CpuPer100Percent`, `Rates__MemoryPerGb`, `Rates__DiskPerBlock`, and `Rates__DiskBlockGb` define the rate card. Decimal values use a period.

`HostingPlans__Prices__zomboid-4gb` is an optional monthly override. `CharmPricing` controls the .99 ending for calculated prices. Form limits are in `HostingPlans__CustomBuild__...`; these constrain estimate requests, without guaranteeing dedicated capacity.

The formula, eight tiers, and period policy are in [technical design](../phase-2-design/technical-design.md).

## Containers

Production uses `deploy/docker-compose.production.yml` with `env_file.format: raw`, requiring a Compose version supporting that format. Deployment env files use literal KEY=value lines without outer quotes or expansion, as described in [DEPLOYMENT.md](../DEPLOYMENT.md).

The two root Compose files are older local alternatives with different services/ports. Specify `-f` explicitly; their defaults are not the official deployment composition. Real secrets must not enter images, commits, logs, or documentation.

## Trials, SMTP and announcements

`Trials` configures the shared 24-hour/72-hour policy, resource limits and approved Minecraft profiles. `Smtp` carries HTS mail delivery configuration; panel passwords are set in Authentik, as described under panel sign-in below. `MinecraftPlans:Tiers` contains agreed paid conversion tiers and defaults empty. `PublicPromotions:Campaigns` permits at most three existing database codes to be advertised; it neither creates coupons nor sets discounts. Use the native `Section__Key` environment names in `.env.example`. New trial requests default off. See [trial configuration](../TRIAL-SERVERS.md).

## Panel sign-in (Authentik)

| Variable | Purpose |
| --- | --- |
| Authentik__BaseUrl | Public HTTPS origin of Authentik, without a path; one-time password links point here |
| Authentik__ApiToken | Service-account token; backend only |
| Authentik__CustomerGroup | Existing customer group, default `hts-customers`; must equal the panel's `AUTHENTIK_CUSTOMER_GROUP` |
| Authentik__CustomerPath | Directory path for customers created by the site, default `customers/hts` |
| Authentik__TimeoutSeconds | API timeout, 2–60 seconds, default 15 |

Configure both the URL and the token, or neither; startup validation rejects anything else. Without them the **Set panel password** button stays hidden. Setup steps and safeguards: [panel sign-in](../TRIAL-SERVERS.md#panel-sign-in).
