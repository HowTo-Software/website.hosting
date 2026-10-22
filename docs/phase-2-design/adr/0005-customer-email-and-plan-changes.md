# 0005 — Verified customer email and reconciled same-server plan changes

Status: implemented locally; production/staging validation and team review pending.
Date: 2026-10-18.

## Context

The marketing site already performs checkout and verified trials, but it has no production
password-based customer identity. Paid service IDs, panel usernames and query-string IDs are
not proof of ownership. Customers need invoices, support and upgrades/downgrades without
exposing the Pterodactyl Application API or changing their server identity.

## Decision

Use the checkout email as the verified customer boundary. Reuse SMTP to deliver a short-lived,
single-use challenge, confirmed only by CSRF-protected POST. Store only token hashes and issue
a distinct revocable session cookie. Resolve resource ownership on every mutable endpoint.

Keep billing authority in Stripe: use native invoice previews and official Price IDs, pending
paid upgrades and renewal schedules for downgrades. Persist a per-service operation with an
exclusive lease and provider idempotency keys. Webhooks and a worker reconcile current provider
state. Only paid authoritative state permits a build update of the existing owned panel server.
Unknown disk usage or ambiguous financial outcomes require operational resolution.

Keep new financial requests disabled by default until staging validation. A Development-only
empty interface preview has no session, mock customer data or action privileges.

## Consequences

The website gains a secure billing/support boundary without receiving panel passwords.
The customer area and Pterodactyl remain separate products. SMTP is an access dependency;
mail outages can delay login, and staff respond to support requests by email.
Commerce migrations and controlled feature activation are required.

Provider failures are not atomic across Stripe, SQL and the panel. Durable intent, bounded
operation budgets, idempotency, invoice proof, leases and reconciliation make recovery explicit.
Requests with uncertain outcomes stay fenced; unknown usage is not treated as empty disk.

## Alternatives

- Password accounts/SSO: broader identity lifecycle and provider integration, not configured here.
- URL IDs or panel email alone: rejected because knowledge of an identifier is not ownership.
- Local proration arithmetic: rejected because it can omit taxes, credits and rounding.
- Immediate downgrades or a replacement server: rejected to preserve the paid period and files.
- Schedule cancellation: rejected because release must preserve the live subscription.

## Sources and validation

- [Customer-area guide](../../CUSTOMER-AREA.md) and the application/provider source map there.
- [Stripe SDK subscription update options](https://github.com/stripe/stripe-dotnet/blob/master/src/Stripe.net/Services/Subscriptions/SubscriptionUpdateOptions.cs).
- [Stripe SDK pending-update entity](https://github.com/stripe/stripe-dotnet/blob/master/src/Stripe.net/Entities/Subscriptions/SubscriptionPendingUpdate.cs).
- Hermetic state-machine, endpoint, persistence, panel HTTP-boundary and browser checks.
- Real SQL Server staging, Stripe test-clock and controlled SMTP/panel integration remain deployment gates.
