# Customer area and subscription management

Status: implemented and tested locally with isolated providers. Production SQL Server,
SMTP delivery, Stripe test-mode billing and Pterodactyl staging must be validated before
financial changes are enabled. No real charge, email or panel operation was executed.

Optional external identities are documented in [EXTERNAL-IDENTITIES.md](EXTERNAL-IDENTITIES.md).
They are off by default. The current email flow remains the primary access/recovery path;
the on-premises Active Directory transport adapter is explicitly pending.

## Scope and navigation

`/account` is the website's customer area, not a replacement for Pterodactyl.
Its tabs are overview, services, invoices, manage plans, support and account. The header
keeps a separate external game-panel link. Copy is localized in English and Brazilian Portuguese.
The interface uses the existing HTS graphite/cobalt themes, Archivo and JetBrains Mono.

Services are derived from commerce orders and the hosting service records belonging to the
verified checkout email. Subscription status is not advertised as game-server online status.
Invoice history and upcoming estimates come from Stripe, including credits, taxes and discounts.
Invoice/payment/PDF links must use exact HTTPS Stripe hosts; arbitrary provider URLs are rejected.
Upcoming invoices are estimates, not guaranteed final amounts.

Support requests are stored durably and delivered to `Site:ContactEmail` by the worker. A relay
failure does not discard a saved request. Delivery is at least once: after a crash, a notification
can repeat with the same support reference. Staff reply by email. This does not implement an
administrator ticket console, live chat, password login or Pterodactyl SSO. Optional Discord
linking and sign-in are implemented separately but disabled by default; see
[EXTERNAL-IDENTITIES.md](EXTERNAL-IDENTITIES.md).

## Secure email access

1. POST `/account/access/request` with a native antiforgery token and the checkout email.
2. A cryptographically random, 15-minute challenge is emailed using the existing SMTP transport.
   URLs use configured `APP_BASE_URL`/`Site:BaseUrl`, never untrusted Host headers.
3. GET `/account?verify=...` only renders confirmation. Email scanners cannot consume the link.
4. POST `/account/access/confirm` atomically consumes the challenge and creates a separate,
   random 30-minute session. Only SHA-256 hashes persist. A link cannot be replayed.
5. The host-only `HTS.CustomerSession.v1` cookie is HttpOnly, SameSite Strict, scoped to
   `/account`, and Secure when HTTPS is effective (including trusted forwarded HTTPS).
   Production must use HTTPS. Private responses are no-store and no-referrer.
6. Every mutation resolves the session again. A service/change ID alone grants no authority.
   POST logout revokes the database session. A subsequent sign-in replaces the previous session.

Requests have per-IP rate limits and a database-enforced email cooldown. Responses do not
enumerate customers. Different emails never share service, billing or support queries.
The existing trial capability remains separate; trial recovery continues to work as before.

## Paid plan changes

New requests require `SubscriptionManagement__Enabled=true`, reconciliation, the customer
area, commerce SQL Server, SMTP, and Stripe API/webhook configuration. Defaults are off.
The service only accepts a plan ID; it obtains the resource limits and Stripe Price ID server-side.
The current subscription must be active, automatically collected, single-item, quantity one,
with a paid current invoice and no conflicting pending update/schedule.

Only compatible plans for the same game and recurring interval are accepted:
monthly/month/1, quarterly/month/3, annual/year/1. Switching intervals is explicitly blocked.
Metered, tiered, mixed-direction resource changes and complex schedules require staff review.
Operator-edited limits and unavailable capacity can also block an automatic upgrade.

### Upgrade

- Fetch Stripe's native next-invoice and recurring previews. Store the exact minor-unit amount
  and proration timestamp; do not calculate proportional billing in JavaScript or locally.
- Quotes expire after five minutes by default. Revalidate ownership, cycle, current invoice,
  capacity and the frozen-time preview on confirmation. A renewal boundary invalidates the quote.
- Durably save intent before calling Stripe with `always_invoice`, `pending_if_incomplete`
  and a stable operation-specific idempotency key. Keep the billing anchor unchanged.
- A redirect or an unpaid invoice cannot change resources. A reconciliation pass must freshly
  confirm the subscription's target price and an owned, paid invoice containing that price.
  A failed payment leaves current limits intact; the hosted Stripe invoice can complete payment.
- Patch the existing Pterodactyl server build only. Verify the stored ID/UUID and exact panel
  owner. Preserve the node, egg, allocation, swap, threads, IO, OOM setting, files and credentials.
  Update the hosting service's plan only after the panel confirms the requested limits.

### Downgrade

- Create a Stripe Subscription Schedule retaining the current phase until renewal, then use the
  target price in the next phase. There is no mid-cycle reduction or improvised refund.
- Preserve supported tax/discount/payment settings. Complex Connect/extra-item schedules are
  rejected for operational review rather than silently discarding their settings.
- Cancellation uses schedule **release**, never schedule cancellation, so the subscription lives on.
  Cancellation must precede the effective renewal and is also durably recorded/reconciled.
- Apply limits only after Stripe actually switches price and a paid target-price renewal line
  covers the new period.
- The Application API reports allocated disk, not actual file usage. Automatic disk reduction
  is therefore refused: mark the change `Operational`, keep all files and current disk, and
  have staff verify usage and safely resolve it. Do not clear the exclusivity fence prematurely.

### Recovery and operational states

`subscription_plan_changes` has one active change per service and lease-token concurrency
fencing. Stripe keys are stable across retries. Signed webhooks trigger fresh authoritative
reads; the worker also reconciles missing/duplicate/out-of-order deliveries.
Panel failures retry the same build update without charging again. An unknown financial
submission older than 23 hours is fenced for operational review instead of reissuing a charge
after Stripe's idempotency retention window. An unverified/missing invoice never grants resources.

For an `Operational` record, compare Stripe's subscription, invoices and schedule against the
existing panel server and SQL record. Protect saves first. Resolve the actual provider state
before marking a change applied/cancelled and freeing its active fence. There is deliberately no
public/admin HTTP endpoint allowing a client to override that safety decision.

## Configuration and migration

All settings are included in `.env.example`; do not commit `.env`.

| Variable | Default | Purpose |
| --- | --- | --- |
| `CustomerArea__Enabled` | `false` | Real email sessions, account data and support |
| `CustomerArea__PreviewEnabled` | `false` | Development-only empty UI preview; no authorization |
| `SubscriptionManagement__Enabled` | `false` | Accept new financial changes |
| `SubscriptionManagement__ReconciliationEnabled` | `false` | Recover/apply existing changes |
| `SubscriptionManagement__PollSeconds` | `60` | Worker cadence; range 15–3600 |
| `SubscriptionManagement__QuoteMinutes` | `5` | Quote lifetime; range 1–10 |

Reuse the existing SQL Server, Stripe, SMTP, Site and Pterodactyl variables. Official Price IDs
come from active `plan_billing_prices` rows, or the existing `Stripe:PriceIds` mapping.
There are no new frontend secrets. Support mail uses the configured site contact address.

Additive migration: `20261018191813_AddCustomerSubscriptionManagement`.
It creates `customer_access`, `customer_support` and `subscription_plan_changes`, indexes and
foreign keys. Existing orders, trials, hosting services and payment records are not rewritten.
New timestamps use UTC ticks in bigint columns; original timestamp columns are unchanged.

Run the existing explicit migration command in an approved deployment job, after backup:

```sh
dotnet HowToSoftware.Hosting.dll --migrate-commerce
```

Do not enable flags against the old schema. To stop new plan changes while recovery continues,
turn `SubscriptionManagement__Enabled` off but leave reconciliation on. Do not roll back/drop
these tables while changes are pending.

## Deployment checklist

1. Back up SQL Server, review the generated migration and apply it explicitly in staging.
2. Configure the canonical HTTPS origin, AllowedHosts and only the actual trusted proxy IPs.
3. Validate SMTP verification/expiration/logout with controlled recipients. Confirm no token/query
   logging in the reverse proxy, telemetry or error pipeline; restrict support-data access/retention.
4. Verify official Stripe prices match each game, interval and currency. Enable the existing signed
   subscription/invoice webhooks. Use test keys, test customers and Stripe test clocks.
5. Test native proration near renewal, declined/late payment, schedules, cancellation release,
   duplicate events, SQL restarts and panel outages. Check the same server UUID/files are preserved.
6. Validate actual Pterodactyl capacity reporting and permissions. Disk reduction remains operational.
7. Enable the customer area first; enable reconciliation and financial requests only after the above.
8. Confirm browser hard refresh, fonts, CSP, native forms, EN/PT-BR, keyboard, dark/light and mobile.

Local interface preview: in Development set `CustomerArea__PreviewEnabled=true` and visit
`/account?preview=true`. It shows empty states with an explicit preview banner. It never creates
a session, shows fake financial data or authorizes any POST. Production ignores preview URLs.

## Code map and tests

- UI: `Components/Pages/CustomerAccount.razor(.css)`, `Components/Pages/Trial.razor(.css)`.
- Access/CSRF routes: `Endpoints/CustomerEndpoints.cs`, `Services/Subscriptions/CustomerAccessService.cs`.
- Billing/support reads: `Services/Subscriptions/CustomerWorkspace.cs`.
- Financial coordination: `Services/Subscriptions/SubscriptionManagementService.cs`, `SubscriptionStore.cs`.
- Provider boundaries: `Infrastructure/Stripe/StripeSubscriptionGateway.cs`,
  `Services/Subscriptions/PterodactylSubscriptionResources.cs`.
- Persistence: `Models/Commerce/CustomerAreaRecords.cs`, `Data/CustomerAreaModelConfiguration.cs`,
  `Data/CommerceMigrations/`.
- Translations: `Localization/CustomerText.resx` and `.pt-BR.resx`.
- Trial identity/time: `Services/Trials/TrialServerIdentity.cs`, `TrialTime.cs`,
  `wwwroot/js/customer-ui.js`.
- Tests: `CustomerEndpointTests`, `CustomerAreaPersistenceTests`, `SubscriptionManagementTests`,
  `TrialIdentityTests`, `TrialTimeTests` and the existing provisioning/trial tests.

The relational tests use an isolated SQLite database for SQL behavior; they do not claim to
replace a real SQL Server staging migration/integration run. Test doubles never use real provider
credentials. Run the opt-in SQL Server integration suite only against a dedicated test database.
