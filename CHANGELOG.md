# Project changelog

> **Status:** Checked against the local code; team review pending
>
> **Owner:** HTS / HowToSoftware maintainers
>
> **Last updated:** 2026-10-05

This changelog records the current state and known local work. No release tag has been assigned to the redesign in this document.

## Unreleased — production readiness (2026-10-18)

- Based the production-readiness branch on the current GitHub main, including the merged redesign, checkout promotion and Pterodactyl pagination fixes.
- Updated EF Core, ASP.NET Core test hosting and dotnet-ef to 10.0.13, TimeProvider.Testing to 10.11.0, and Microsoft.NET.Test.Sdk to 18.11.0. Aligned the explicit cryptography package with .NET 10 at 10.0.13.
- Added the production storefront hostname to the default host allowlist. Compose now preserves private host configuration; local Compose also uses raw env files so passwords are not interpolated or overwritten.
- Corrected the antiforgery cookie name for direct HTTP deployments, retaining HttpOnly, SameSite=Strict, a host-only scope and Secure on HTTPS.
- Added production tests for hostname filtering, real CSRF form round trips and trusted/untrusted proxy behavior.
- Added a reproducible published-runtime smoke check for uncached refreshes, Blazor/JS/CSS/font assets, public routes, antiforgery and HTTPS proxy headers. CI runs it inside the actual nonroot/read-only image before deployment and rejects reported NuGet vulnerabilities.
- Updated deployment instructions with the private host/proxy configuration requirements and the boundary between isolated runtime checks and real staging integrations.

Validation: Release build and publish without warnings/errors, 639 passing automated tests,
100 isolated published-runtime smoke assertions, and 27 Playwright assertions across desktop
and mobile with no browser errors or failed asset requests. NuGet reported no outdated direct
packages or known vulnerabilities, including transitive dependencies. Real SQL, SMTP, Stripe
and Pterodactyl operations were not invoked. The container smoke gate must also pass on the
GitHub-hosted Docker runner before merging; Docker was unavailable on the local workstation.

## Previous delivery — visual redesign and documentation

### Frontend

- New storefront hierarchy: game → plan → review → payment, with links generated from existing routes.
- Graphite, paper, and cobalt palette; light/dark themes; improved navigation, comparison, review, and infrastructure presentation.
- Large HTS wordmark centered at the beginning above the heading; removed the large configurator and homepage panel demonstration.
- Compact infrastructure page with real photos, a single specification sheet and native operational disclosures.
- Trial entry prioritised on the homepage; conditional announcements for explicitly approved database promotion codes.
- HTS Vector Wordmark adapted from the supplied OriginKit code, using WebGL with pausing, drawing limits, and a static fallback.
- Skiper UI CssLink and TextRoll adaptations, with collected sources and preserved attribution.
- CSS masks and updates that preserve Blazor's node tree during navigation.
- Reduced-motion and no-JavaScript support, with responsive refinements.
- Storefront copy in EN/PT-BR, maintaining parity across updated resource families.

### Automatic trial lifecycle

- Added SQL-backed, email-verified trials shared across Project Zomboid and Minecraft: one per account, 24 hours from installation completion, then suspension and 72-hour save retention.
- Defaults: CPU 0, RAM 6144 MiB and disk 25600 MiB; operator configuration governs approved Minecraft editions, variants, versions and eggs.
- Added SMTP notifications, owner access recovery, durable lifecycle leases, retries and paid-order associations.
- Checkout conversion updates and resumes the same server. Expired reservations retain order history, preventing late payments from creating a second server.
- Added an unapplied commerce migration and environment examples; no production database or server was changed.
- Minecraft paid tiers remain configuration-driven, with no invented commercial prices.

### Documentation

- Rewritten README distinguishing features, integrations, demonstrations, and outstanding work.
- Six-phase index inspired by `best-document`, covering architecture, requirements, development, QA, deployment, and operations.
- Data model, HTTP contracts, ADRs, user guides, and file map.
- Corrected outdated descriptions of plans, prices, animations, the lab, and production state.
- Existing technical guides retained as references and linked from the new index.
- English applied throughout the generated and revised documentation, including metadata, diagrams, and link labels; English established as the maintenance language.

### Visual review evidence

Before publication, incorporated the latest `main` dependency updates at `277e7b1` and repeated Release build/test validation. Updated repository presentation and current v3 screenshots, documented local `.env` placement, excluded env files from Docker context, and corrected the operations guide to reflect minute-based order recovery.

NuGet checks reported no outdated direct packages and no vulnerable packages, including transitive dependencies, in the current feed. Internal dependency upgrades were not forced beyond the versions selected by their parent SDKs.

On 2026-10-05: Release compilation without warnings/errors, 627 passing tests, and 77 Chromium checks in an isolated local environment. Current screenshots and reports capture the centered opening wordmark and compact infrastructure. See the [detailed record](docs/FRONTEND-REDESIGN.md) for scenarios and limitations.

## Previous main baseline

The local redesign baseline is `a7ac383`: the deployment directory moved to `/opt/howtoosoftware-hosting-prototype`. That baseline already contains Stripe checkout, SQL Server persistence, Pterodactyl provisioning, and the deployment pipeline; these features are not presented as part of a new visual release.

Commit history is the source for authorship and change order: use `git log`. When releasing a version, add its tag/commit, changes, and actual validation using the [release notes guide](docs/phase-5-deployment/release-notes-template.md).
