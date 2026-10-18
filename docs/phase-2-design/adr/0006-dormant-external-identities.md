# 0006 — Dormant external identities without replacing HTS email access

Status: implemented locally; provider staging and team review pending.
Date: 2026-10-18.

## Context

HTS needs a future on-premises Active Directory integration and Discord account linking,
including optional Discord login. Existing email access must keep working. The directory
transport is not yet known; Hank confirmed on-premises AD, not Microsoft Entra ID.

## Decision

Leave all external providers and external login flags off. Explicitly block directory activation
until AD FS/OIDC versus LDAPS is confirmed and the corresponding adapter is implemented.
Do not guess a tenant, add a service account, accept domain passwords or contact a directory.

Persist external bindings to the existing stable HTS account by provider/issuer/subject,
never by email/name claims. A native HTS session is required to initiate linking. Discord
uses framework authorization-code OAuth with PKCE, protected state/correlation and server-side
subject lookup, then explicit browser/session-bound CSRF-protected confirmation.

Future external login resolves only existing bindings and rotates the native opaque session.
Do not save provider tokens, issue an implicit external-session cookie, assign provider roles,
auto-provision customers or change commerce/Pterodactyl authorization.

## Consequences

No external credentials/schema reads/network activity are required by default. Future staging
activation requires explicit configuration and migrations. The same-origin return step preserves
Strict native cookies instead of weakening their policy for third-party redirects.
An external binding cannot silently move between HTS accounts. Email remains the recovery path.

The AD transport is intentionally not ready to enable. Discord staging authorization and
real SQL Server migration validation remain rollout gates. Future bot/API integrations need
their own permissions and data-access design; this feature grants none.

## Alternatives

- Replace email authentication or enable a provider immediately: conflicts with requested rollout.
- Assume Entra ID or silently install AD FS/LDAPS: conflicts with the confirmed infrastructure and
  unresolved transport choice.
- Merge by matching external email: rejected because it can transfer another customer's services.
- Persist OAuth refresh tokens for unspecified future uses: rejected as unnecessary credential exposure.

## Sources and validation

- [External identities guide](../../EXTERNAL-IDENTITIES.md), including the source map and rollout checklist.
- [Discord OAuth2](https://docs.discord.com/developers/topics/oauth2).
- [ASP.NET Core authentication guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/).
- Native-access regression tests, relational identity/ownership tests and real OAuth middleware tests
  using only an isolated fake HTTP provider.
