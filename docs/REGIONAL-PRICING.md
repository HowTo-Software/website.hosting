# Regional pricing and Stripe Pix

Status: implemented and locally tested with isolated payment/database fixtures. No live
Stripe capability was activated, money collected or production migration applied.
On 2026-10-18, HTS confirmed a US-registered Stripe account and chose to absorb Pix IOF,
keeping the advertised BRL total. These choices are recorded in public configuration;
the real account capability and staging payment flows have not yet been verified.

## Price and charge currency

The configured catalogue remains USD. `StorefrontCurrencyService` applies the operator-owned
fixed rate `HostingPlans:RegionalPricing:BrlPerUsd`, shipped as `6.50`.
This is not a live market feed or Stripe Adaptive Pricing.

The public storefront defaults to BRL when the browser's highest-priority, non-zero
`Accept-Language` preference explicitly identifies Brazil, for example `pt-BR` or `en-BR`.
It defaults to USD for missing/ambiguous preferences, bare `pt`, `pt-PT`, or an English-US
preference ahead of a lower-priority Brazilian entry. Browser language is a preference,
not proof of location, residence, nationality, Stripe eligibility or billing country.

The language cookie does not change the detected regional currency. A Brazilian browser can
read the site in English and still see BRL. Review offers an explicit USD/BRL choice before
payment. Links, native POSTs and Stripe cancellation URLs preserve that choice.
HTML responses vary on `Accept-Language` and `Cookie` to prevent cross-visitor cache mixing.

All amounts are calculated again on the server. The browser supplies only an allowlisted
currency identifier, not the rate, amount, discount or Stripe Price ID. Orders snapshot the
selected currency and computed amounts. BRL orders send `currency=brl` to Stripe, and the
signed payment-event path verifies both the exact expected minor-unit total and currency.
The success-page redirect and a QR code never authorize provisioning.

### Rounding and discounts

Compute the existing USD period quote first, including the existing 5% quarterly or 10%
annual discount. Multiply each authoritative period total by 6.50 once and round to two
decimal places, midpoint away from zero. Do not reapply charm pricing or multiply an already
converted amount again. Derive displayed discount amount as converted base minus final,
so the breakdown adds up. Do not reprice the whole period from a rounded BRL monthly label.

For the current USD 7.99 starting plan:

| Period | USD total | BRL total |
| --- | ---: | ---: |
| Monthly | 7.99 | 51.94 |
| Quarterly, 5% discount | 22.77 | 148.01 |
| Annual, 10% discount | 86.29 | 560.89 |

BRL uses Brazilian numeric formatting. Existing USD orders, invoices, renewal estimates,
subscriptions and saved plan changes retain their original currency and amounts. Display
preferences never convert financial history or change an existing subscription's currency.
New subscriptions also remain subject to Stripe's customer/currency compatibility rules.

## Price mappings, coupons and persistence

Pre-created Stripe prices use `{plan-slug}:{period-slug}:{currency}` keys in `Stripe:PriceIds`,
for example `zomboid-4gb:monthly:brl`. Old two-part keys remain USD-only; they are never reused
for BRL. Without a mapped price, checkout constructs the server-priced inline recurring item
in the selected currency. Configured Stripe prices must match the exact amount and currency.

`plan_billing_prices` now has a unique `(plan_id, billing_period, currency)` index. The additive
migration is `20261018234316_AddRegionalBillingCurrencies`; it changes indexes only, not orders
or monetary data. Apply it explicitly in an approved, backed-up staging/deployment job before
adding BRL mappings. It has not been applied to a live database by this implementation.

The Development-only seed creates both USD and BRL rows for each period and preserves each
row's independent Stripe identifiers across reruns. Do not run a development seed against
production or use it to overwrite operator-maintained commercial values.

Subscription plan lookup uses the existing order/subscription currency. Switching currency
as part of an upgrade/downgrade is not implemented. Historical USD subscriptions stay USD.
Currency-specific fixed coupons/minimums do not become amounts in another currency; legacy
unqualified fixed/minimum coupons remain USD-only. Explicit currency-compatible percent
coupons can apply to the server-priced quote. Public announcements use the same validation.

Do not automatically roll back the index migration after dual-currency rows exist: the old
unique index cannot represent them. Review a data-preserving rollback plan first.

## Pix subscription preparation — disabled by default

Stripe's current [Pix documentation](https://docs.stripe.com/payments/pix) distinguishes
one-time Pix and recurring Pix Automático. Brazilian-domiciled Stripe accounts support
one-time Pix but not Pix Automático; eligible non-Brazilian accounts can support recurring
Pix. Account activation and availability must be confirmed in Stripe; a configured country
string does not prove the account has the capability.

This site uses subscription Checkout. It does **not** silently switch subscriptions to
one-time payments or implement a new manual-prepaid renewal/suspension workflow. A future
move to a Brazilian-domiciled Stripe account would require a separately approved billing design.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Stripe__PixSubscriptions__Enabled` | `false` | Explicit opt-in after eligibility/staging validation |
| `Stripe__PixSubscriptions__AccountCountry` | `US` | Confirmed US-registered HTS Stripe account |
| `Stripe__PixSubscriptions__AmountIncludesIof` | `always` | Confirmed policy: HTS absorbs IOF; no customer surcharge |

No country or tax decision is inferred from a browser or silently selected for HTS.
The operator-confirmed values are in `appsettings.json` and `.env.example`; the real `.env`
was not changed. Deployment overrides must retain `US` and `always` unless HTS approves a
different policy. The application does not calculate or add a separate IOF amount to prices;
Stripe handles it under `always` for both the initial payment and subsequent mandate payments.
Enabling without an eligible country and explicit IOF policy fails closed before creating
an order, reserving a trial or contacting Stripe. Pix is excluded for USD sessions and when
the feature is off; other payment methods remain dynamically managed by Stripe.

When explicitly enabled for eligible BRL subscription sessions, the SDK sends Pix options
with the chosen IOF policy for initial and mandate payments, the exact server-priced total
as the mandate maximum, BRL currency, and matching monthly/quarterly/yearly cadence.
It does not use an unrelated default BRL 400 mandate limit or authorize higher future charges.
Stripe's hosted flow handles the customer's bank authorization and required disclosures.
Configured options do not activate the payment method in the merchant Dashboard.

Per the [Pix Automático guide](https://docs.stripe.com/payments/pix/pix-automatico), recurring
payments include a bank pre-debit notification and can remain processing for several days.
Unpaid/processing Checkout sessions remain pending. Fulfilment waits for verified payment;
duplicate, late and failed events must be tested with the account's actual capability.

The existing self-service plan-change workflow is not a Pix-mandate updater. It refuses new
changes for Pix-funded subscriptions, including inherited customer defaults and payment-method
changes after quoting, without altering the current server or charging. Support must review
the mandate before a different recurring amount can be authorized.

## Activation and validation checklist

1. Verify Pix capability and current requirements on the confirmed US Stripe account.
2. Retain the confirmed merchant-paid IOF setting `always`. Do not hardcode a tax rate in UI code.
3. Apply the index migration to isolated SQL Server staging and configure separate USD/BRL prices.
4. Keep the real `.env` private. Use deployment secret/configuration management for Stripe settings.
5. Validate real BRL card checkout in test mode and verify snapshot/Stripe amounts and currency.
6. If eligible, activate Pix in Stripe's payment-method settings in test mode and then enable the
   app flag with the confirmed country/IOF settings. Do not advertise Pix until validated.
7. Test bank enrollment, unpaid QR, successful/expired/replayed events, recurring processing,
   mandate limits, invoice visibility and cancellation without creating duplicate servers.
8. Verify the same-world trial upgrade and current email recovery flow are preserved.
9. Obtain explicit production approval. Do not merge or activate solely because local tests pass.

Core tests: `StorefrontCurrencyTests`, `RegionalCurrencyPersistenceTests`,
`RegionalPromotionTests`, `StripePixTests`, `PurchaseFlowTests`,
`CustomerAreaPersistenceTests` and `SubscriptionManagementTests`.
They use fake provider responses and isolated SQLite, not a real Stripe account or production SQL.
