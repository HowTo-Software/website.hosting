# Architecture decision records

> **Status:** Checked against the local code; team review pending
>
> **Owner:** HTS / HowToSoftware maintainers
>
> **Last updated:** 2026-10-05

These ADRs document decisions already present in the code. They are **retrospective records**, with formal team review pending; their documentation date is not the original adoption date.

| Record | Decision |
| --- | --- |
| [0001](0001-blazor-ssr.md) | Blazor SSR with component-level interactivity |
| [0002](0002-server-authority.md) | Server and webhook financial authority |
| [0003](0003-persistence-and-migrations.md) | SQL Server, explicit migrations, and order-based recovery |
| [0004](0004-preserve-blazor-dom.md) | Motion that preserves Blazor's DOM |
| [0005](0005-customer-email-and-plan-changes.md) | Verified customer email and reconciled same-server plan changes |
| [0006](0006-dormant-external-identities.md) | Dormant AD/Discord identity boundary; keep native email access |

For a new decision, add a numbered file with context, decision, consequences, alternatives, and sources/validation. If replacing a decision, retain the old record and link to the new one; do not erase its historical rationale.
