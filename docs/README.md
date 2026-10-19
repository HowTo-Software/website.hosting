# HTS Hosting documentation

> **Status:** Checked against the local code; team review pending
>
> **Owner:** HTS / HowToSoftware maintainers
>
> **Last updated:** 2026-10-05

This is the starting point for understanding the product, changing the code, and operating the application. Its organization follows the six phases of **best-document**, provided at `C:\Users\Malaio\Desktop\best-document`, with content filled from this repository.

## Find the explanation for your task

| I need to… | Start here |
| --- | --- |
| Understand the product and its limits | [Vision and scope](phase-1-inception/vision-and-scope.md) |
| Understand everything changed by the visual redesign | [Frontend redesign](FRONTEND-REDESIGN.md) |
| Run the project for the first time | [Onboarding](phase-3-development/onboarding.md) |
| Configure URLs, prices, database, and integrations | [Configuration](phase-3-development/configuration.md) |
| Locate components and services | [Project map](PROJECT-MAP.md) |
| Understand purchases, webhooks, and server creation | [Architecture](phase-2-design/architecture.md) and [commerce](COMMERCE-ARCHITECTURE.md) |
| Look up tables, states, and migrations | [Data model](phase-2-design/data-model.md) |
| Look up pages and HTTP contracts | [Routes and endpoints](phase-2-design/api-specification.md) |
| Validate changes | [Test plan](phase-4-testing/test-plan.md) |
| Deploy or return to an earlier image | [Deployment](DEPLOYMENT.md) and [CI/CD](phase-5-deployment/ci-cd.md) |
| Diagnose production failures | [Runbook](phase-6-operations/runbook.md) |
| Recover services and data | [Disaster recovery](phase-6-operations/disaster-recovery.md) |
| Use the site as a customer | [User guide](user/user-guide.md) |
| Configure verified customer access, invoices, support and plan changes | [Customer area](CUSTOMER-AREA.md) |
| Prepare dormant on-premises AD and Discord identities | [External identities](EXTERNAL-IDENTITIES.md) |
| Configure actual USD/BRL checkout and eligible Pix subscriptions | [Regional pricing](REGIONAL-PRICING.md) |

## Project phases

| Phase | Documents |
| --- | --- |
| 1. Inception and requirements | [Vision](phase-1-inception/vision-and-scope.md), [product](phase-1-inception/product-requirements-document.md), [software requirements](phase-1-inception/software-requirements-specification.md), [stories](phase-1-inception/user-stories.md), [roadmap](phase-1-inception/product-roadmap.md) |
| 2. Architecture and design | [Architecture](phase-2-design/architecture.md), [technical design](phase-2-design/technical-design.md), [data](phase-2-design/data-model.md), [HTTP](phase-2-design/api-specification.md), [diagrams](phase-2-design/diagrams.md), [ADRs](phase-2-design/adr/README.md) |
| 3. Development | [Onboarding](phase-3-development/onboarding.md), [configuration](phase-3-development/configuration.md), [standards](phase-3-development/coding-standards.md), [Git](phase-3-development/git-workflow.md) |
| 4. Quality | [Plan](phase-4-testing/test-plan.md), [cases](phase-4-testing/test-cases.md), [bug reports](phase-4-testing/bug-report-template.md) |
| 5. Deployment | [Guide](phase-5-deployment/deployment-guide.md), [pipeline](phase-5-deployment/ci-cd.md), [release notes](phase-5-deployment/release-notes-template.md) |
| 6. Operations | [Runbook](phase-6-operations/runbook.md), [monitoring](phase-6-operations/monitoring.md), [recovery](phase-6-operations/disaster-recovery.md) |
| Security | [Threats](security/threat-model.md), [data and compliance](security/compliance.md), [hardening](SECURITY-HARDENING.md), [private reporting](../SECURITY.md) |
| Users | [Guide](user/user-guide.md), [FAQ](user/faq.md), [troubleshooting](user/troubleshooting.md) |

## Preserved specialist guides

[FRONTEND-REDESIGN.md](FRONTEND-REDESIGN.md), [COMMERCE-ARCHITECTURE.md](COMMERCE-ARCHITECTURE.md), [DEPLOYMENT.md](DEPLOYMENT.md), [SQLSERVER-SETUP.md](SQLSERVER-SETUP.md), [PTERODACTYL-SETUP.md](PTERODACTYL-SETUP.md), and [stripe-testing.md](stripe-testing.md) retain their existing paths. Phase documents link to these guides when the procedure already exists.

## Interpreting document status

**Checked against the local code** means that behavior was compared with files in this working tree. It does not imply formal team approval or verification of external infrastructure.

**Operational procedure** describes how to perform a task; applying it on the server depends on the real configuration. **Proposal without a schedule** identifies desired work without a delivery commitment. Historical evidence retains its execution date and environment.

The working branch is `feat/hosting-frontend-redesign`, based on `main` at `a7ac383`. Documented local changes may not have been published yet. The [changelog](../CHANGELOG.md) uses “Unreleased” for this work.

Maintain all project documentation in **English**, following the [adopted standard](documentation-standard.md). These guides describe **HTS Hosting**, which is separate from the other HTS website creation and maintenance project.

## Automatic game trials

[Trial servers](TRIAL-SERVERS.md) documents email verification, the shared 24-hour policy, SQL claims, Pterodactyl suspension, 72-hour save retention, Minecraft selectors and conversion through the existing checkout.

[Public promotions](PUBLIC-PROMOTIONS.md) explains opt-in announcements of existing coupons, validation, limits and checkout links.
