# External identities: on-premises Active Directory and Discord

Status: dormant foundation, 2026-10-18. The existing email-link login is retained.
No production database migration, Discord authorization, directory connection or provider
activation was performed.

## Confirmed scope

- Hank confirmed **on-premises Active Directory**, not Microsoft Entra ID.
- AD FS/OpenID Connect versus direct LDAPS has not been decided. The directory transport
  adapter is intentionally **pending**, as requested. Startup rejects attempts to enable it.
- Discord account linking and optional future Discord login are implemented behind independent
  flags. Both are off by default.
- No directory synchronization, role assignment, Discord bot, guild membership, billing privilege,
  Pterodactyl SSO or server-control permission is introduced.

This is not a claim that the AD transport is ready to enable. Once Hank confirms the protocol,
implement and validate that adapter; reuse the existing HTS identity/proof/session boundaries.

## Identity and authorization boundary

An HTS customer first signs in using the existing verified email link. The stable
`customer_access.id` identifies the account; the current checkout email still scopes commerce
ownership. An external identity is identified by **provider + issuer + immutable subject**,
not a display name, email claim, Discord username, URL parameter or panel user ID.

The database permits one identity from a given provider per HTS account and only one HTS
account per external identity. Linking another subject requires explicitly unlinking first.
An external login works only for a previously linked identity: it never creates customers or
merges accounts by matching email. Customer email changes require a separate verified process;
renaming an external account does not transfer hosting ownership.

## Discord flow

1. The signed-in customer POSTs `/account/connections/start` with `Provider=discord` and CSRF.
2. The server creates a ten-minute intent bound to the exact HTS session and a new HttpOnly,
   Secure, SameSite Strict browser-secret cookie. Only hashes persist.
3. The framework performs authorization-code OAuth with PKCE S256, protected state and
   a correlation cookie. Only `identify` is requested: no email, bot, guild or role scopes.
4. The callback rejects missing/wrong Discord `iss` **before** exchanging the authorization code.
   The server fetches the subject from the fixed Discord `/users/@me` endpoint. Browser-supplied
   IDs, names or emails cannot establish a link.
5. The callback records verified identity proof, not a link or login. It does not create a
   framework sign-in cookie or expose/save access/refresh tokens.
6. The customer follows the same-origin return link, reviews the identity, then POSTs
   `/account/connections/confirm` with CSRF. This final step validates the original browser,
   unexpired proof, and original HTS session before linking.

The intermediate return page is deliberate: existing HTS cookies remain SameSite Strict,
so a cross-site provider callback cannot rely on receiving the native session cookie.
The native cookie policy was not weakened to make OAuth work.
The email challenge's consumption marker remains independent of an already-issued session:
requesting another email does not log the customer out; confirming a new sign-in rotates it.

When future login is separately enabled, `/account/access/external` starts a **login** intent.
The same final browser/CSRF confirmation resolves only an existing identity binding and rotates
the existing opaque HTS session. No provider claims are used as HTS roles or financial permissions.

Unlinking is an owner-authorized CSRF-protected POST. It removes only that customer's binding
and blocks subsequent logins through it, including already-pending login intents. Existing HTS
sessions remain valid until logout/expiry; email recovery remains available.

The integration follows the provider's [authorization-code, state and issuer guidance](https://docs.discord.com/developers/topics/oauth2).
No OAuth token is kept for future bot work: that future project needs its own scoped design.

## Flags and secrets

All settings are in `.env.example`. The real `.env` is untouched.

| Variable | Default | Meaning |
| --- | --- | --- |
| `ExternalIdentity__ActiveDirectory__Enabled` | `false` | Reserved; enabling is blocked until the transport adapter exists |
| `ExternalIdentity__ActiveDirectory__LoginEnabled` | `false` | Reserved future login switch |
| `ExternalIdentity__ActiveDirectory__Protocol` | `Unspecified` | Pending AD FS versus LDAPS decision |
| `ExternalIdentity__ActiveDirectory__Issuer` | empty | Reserved stable directory/federation issuer |
| `ExternalIdentity__Discord__Enabled` | `false` | Register Discord linking/callback only after configuration |
| `ExternalIdentity__Discord__LoginEnabled` | `false` | Separately allow login for already-linked customers |
| `ExternalIdentity__Discord__ClientId` | empty | Discord application ID |
| `ExternalIdentity__Discord__ClientSecret` | empty | Server-only application secret |

Discord activation additionally requires the existing customer area, commerce SQL Server,
SMTP recovery configuration, and canonical HTTPS `APP_BASE_URL`/`Site:BaseUrl`.
The initiating host must match that canonical origin. HTTP/LAN previews keep working with
providers disabled; never activate external login on a public plaintext HTTP origin.

No credentials are required when flags are off. No remote handler, directory query, provider
request, new-table lookup or external-identity cleanup worker runs on behalf of disabled
providers. Changing registration flags/credentials requires an application restart.

Application secrets belong in the deployment secret store, not Razor, JavaScript or logs.
Persist Data Protection keys across replicas/restarts as required by the existing deployment
guide; protected OAuth state must be readable by the replica receiving the callback.

## Additive migration and storage

Migration: `20261018200651_AddExternalCustomerIdentities`, after
`20261018191813_AddCustomerSubscriptionManagement`.

- `customer_external_identities`: account FK, provider, issuer, immutable subject, display
  metadata, linked-at UTC ticks; unique account/provider and provider/issuer/subject indexes.
- `external_identity_flows`: expiring intent, hashes of browser/session proof, verified subject
  and consumption state. No raw OAuth state, token, secret, code, password or email claim is stored.

Existing accounts, orders, subscriptions and servers are not rewritten. Timestamp columns in
these new tables use the same UTC-ticks convention as the customer-area tables.
The enabled Discord cleanup worker removes proofs whose expiry is more than one hour old;
normal proof validity is still only ten minutes. Linked identities persist until unlinked.
Restrict database access to these identifiers and customer data.

Apply migrations only through the explicit, approved commerce deployment job:

```sh
dotnet HowToSoftware.Hosting.dll --migrate-commerce
```

Do not enable Discord against the old schema. Leaving providers off lets the existing
customer/email workflow run without needing the new identity tables. Do not roll back/drop
identity tables with live bindings without an approved customer-access migration.

## Future activation checklist

### Discord

1. Apply the migration to an isolated SQL Server staging database.
2. Create/configure an operator-owned Discord application and store its secret securely.
3. Register exactly `https://YOUR_CANONICAL_ORIGIN/account/connections/callback/discord`.
4. Keep `LoginEnabled=false`; enable linking only in staging and restart.
5. Use controlled accounts to test link review, cancelled consent, replay, expiry, cross-account
   conflicts, browser/session rotation, unlinking and SMTP recovery.
6. Verify provider failures never leak codes/tokens and proxy/telemetry do not log callback
   query strings or sensitive cookies. Configure only real trusted proxies and canonical hosts.
7. Enable the separate login flag only after linked-account login is validated.
8. Authorize a production rollout explicitly; this implementation did not enable production.

### On-premises Active Directory — adapter pending

Confirm with Hank: AD FS/OIDC or LDAPS; directory/federation endpoints; HTTPS/certificate trust;
network reachability from the deployment; immutable user identifier; customer eligibility;
MFA/lockout policy and recovery.

For AD FS, use a validated OpenID Connect handler with issuer/audience/signature/nonce/lifetime,
state/correlation and PKCE checks. For LDAPS, design a server-side credential adapter with strict
TLS verification, safe LDAP filter escaping, scoped directory reads, immutable object identity
and account-aware brute-force protection. Never expose a domain controller publicly just for
this website. Do not assume Windows integrated authentication works across the public web
or the existing Linux container.

No LDAP client, service-account password, implicit Windows authentication, AD FS setup or Entra
tenant dependency was added while that choice remains pending. The explicit startup guard
must be replaced by validated adapter registration only when that implementation is approved.

## Code and validation map

- Models/configuration: `Models/Commerce/ExternalIdentityRecords.cs`,
  `Data/ExternalIdentityModelConfiguration.cs`.
- Flags/contracts/registration: `Services/Identity/ExternalIdentityOptions.cs`,
  `ExternalIdentityRegistration.cs`.
- Proof/link/login persistence: `Services/Identity/ExternalIdentityService.cs`.
- Provider boundary: `Infrastructure/Security/DiscordIdentityHandler.cs`.
- Routes/cookies: `Endpoints/ExternalIdentityEndpoints.cs`.
- UI: `Components/Account/AccountConnections.razor(.css)`,
  `Components/Pages/ExternalIdentityReturn.razor(.css)`, existing `CustomerAccount.razor`.
- EN/PT-BR copy: `Localization/IdentityText*.resx`.
- Tests: `ExternalIdentityPersistenceTests`, `ExternalIdentityOptionsTests`,
  `DiscordIdentityProtocolTests`, and the extended `CustomerEndpointTests`.

Protocol tests exercise the real framework OAuth middleware, but the Discord HTTP backchannel
and relational database are isolated test fixtures. They do not contact Discord, LDAP, SMTP,
Stripe, Pterodactyl or a production SQL Server. Real provider/staging validation remains required.
