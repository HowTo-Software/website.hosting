# Security and production checklist

> **Status:** Controls checked against the code; external infrastructure unverified
>
> **Owner:** HTS / HowToSoftware maintainers
>
> **Last updated:** 2026-10-05

[Index](README.md) · [Threat model](security/threat-model.md) · [Runbook](phase-6-operations/runbook.md)

This document distinguishes application controls from infrastructure configuration. An application alone cannot promise maximum security or absorb volumetric DDoS attacks; defense requires monitored layers.

## Application controls

- **Backend authority:** plan, period, and price are resolved again on the server. Signed Stripe webhooks verify currency and amount before provisioning.
- **SQL injection:** database access uses parameterized EF Core/LINQ. Browser fields do not generate SQL; existing raw SQL is a static, versioned migration.
- **XSS and clickjacking:** Razor encodes content by default. The homepage uses `MarkupString` only for `JsonSerializer`-produced JSON-LD inserted with a nonce, without arbitrary user HTML. Do not render untrusted content raw. CSP uses a random per-response nonce and blocks remote scripts, objects, external `base`, and framing by another site.
- **CSRF:** mutable forms use antiforgery. The host-only, HttpOnly, SameSite=Strict cookie uses SameAsRequest so trusted HTTPS requests emit Secure cookies while direct HTTP LAN forms remain usable. Its name is valid on both transports: a `__Host-` prefix with no Secure flag would be rejected by browsers. The webhook is the exception, using a Stripe signature over the original body.
- **Automated abuse:** order status, webhook, and health checks have per-IP limits without overflow queuing. Webhook bodies and general Kestrel request limits are bounded.
- **Data and logs:** EF Core does not log sensitive values; public status responses exclude price, email, internal order ID, and server identifiers.
- **Lab surface:** manual Pterodactyl creation/deletion requires both `Development` and an explicit flag. The flag cannot open the lab in staging/production.
- **Outbound requests:** Pterodactyl automatic redirects are disabled, the URL requires HTTPS, and the key is placed only in the backend `Authorization` header.
- **Database transport:** SQL Server connections force `Encrypt=True` even if supplied configuration disables it. Certificate validation remains enabled unless the operator explicitly chooses `TrustServerCertificate=True`, which permits interception.

Middleware in [`SecurityHardening.cs`](../src/HowToSoftware.Hosting/Infrastructure/Security/SecurityHardening.cs) centralizes CSP, headers, antiforgery, and rate limits. `SecurityBoundaryTests.cs` protects these boundaries against accidental removal during refactoring.

## HTTPS, TLS, and HSTS

Production redirects HTTP with 308 and sends 365-day HSTS. Local development uses `http://localhost:5147` without requiring a local certificate. Configure the public edge as follows:

1. Minimum TLS 1.2, preferably TLS 1.3, with a valid certificate and automatic renewal.
2. If using Cloudflare/a proxy, enforce strict certificate validation between proxy and origin as well; do not accept plaintext origin transport.
3. Block direct public access to the origin port; allow only the proxy/load balancer.
4. Enable HSTS `includeSubDomains` and preload only after verifying that **all** current and future subdomains are HTTPS-only. Code leaves both disabled for operational safety.
5. Configure known proxies before consuming forwarded headers. Do not trust internet-supplied `X-Forwarded-For` directly.

## End-to-end encryption claims

Card checkout is hosted by Stripe; the site does not receive or store complete card numbers. TLS protects transport between browser, application, Stripe, SQL Server, and Pterodactyl.

This is **not end-to-end encryption** in the private-messaging sense: the backend must read orders to charge and provision. Calling this flow E2E encryption would be inaccurate. Minimize personal data, use provider encryption at rest, and restrict access to services/operators needing it. Exceptionally sensitive fields can use application encryption with KMS-managed keys outside the database.

## WAF, DDoS, and brute force

Before opening production, configure CDN/WAF protections:

- Managed OWASP Core Rule Set rules, initially in observation mode.
- Edge limits for future login, checkout creation, status, and administrative routes.
- Progressive challenges/blocks for bots and repeated attempts.
- Provider DDoS mitigation and traffic/error alerts.
- Reachable Stripe webhook with mandatory signature and conservative application limits.
- Origin database, panel, and SSH ports restricted from public exposure.

Real authentication is not implemented yet, so there are no customer passwords to brute force on this site. When adding accounts, use a reviewed identity provider and its modern password hashing, mandatory administrator MFA, indistinguishable nonexistent-user/wrong-password responses, progressive blocking, and ownership authorization on **every** order/server read or action. Do not turn the current visual form into homemade authentication.

## Database and secrets

- Use a dedicated SQL Server application login, never `sa`, without ownership or runtime DDL permissions. Migrate with a separate identity.
- Give the application its own database. Shared application databases can cause migration-history collisions and schema interference.
- Keep keys in a secret manager, rotate them, and separate test/staging/production.
- Persist ASP.NET Data Protection keys in private, encrypted storage when using multiple instances; losing keys invalidates tokens/cookies across restarts.
- Do not send `.env` through Git, Discord, tickets, or screenshots. `.env.example` contains formats only.
- Configure limited retention, restricted access, and redaction in aggregated logs.

## Pre-deployment verification

```powershell
dotnet build --configuration Release
dotnet test --configuration Release --no-build
dotnet package list --project src/HowToSoftware.Hosting --vulnerable --include-transitive
```

Also test headers/TLS on the final domain, run authorized DAST in staging, restore a backup in isolation, and verify alerts for webhook, provisioning, and login failures. Repeat dependency review and threat modeling for each new route handling customer data.
