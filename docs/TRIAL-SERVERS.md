# Verified trial servers

> **Status:** Implemented locally; production SQL, SMTP and panel execution unverified
>
> **Owner:** HTS / HowToSoftware maintainers
>
> **Last updated:** 2026-10-05

[Index](README.md) · [Commerce](COMMERCE-ARCHITECTURE.md) · [Pterodactyl](PTERODACTYL-SETUP.md) · [Deployment](DEPLOYMENT.md)

HTS trials are a separate workflow from the Development provisioning lab. A customer verifies
email ownership, receives one trial per panel account across all games, and can upgrade the
same server after Stripe confirms payment. The shipped configuration keeps new trial creation
disabled until the operator supplies the required services. No production connection, migration,
email delivery, server creation, suspension or deletion was performed to implement this feature.

## Customer journey

1. Open `/trial` and choose Project Zomboid or Minecraft. Minecraft also requires Java/Bedrock,
   an approved software profile and an approved version. A selection without a configured
   deployment profile is shown as unavailable.
2. POST the name, email and selection to `/trials/request` with antiforgery. The response contains
   a generic inbox message; the email address never appears in a redirect URL.
3. Receive a one-use verification link. GET `/trial?verify=…` displays a confirmation page only;
   link scanners cannot claim a trial or create a server by opening it.
4. POST the token to `/trials/confirm` with antiforgery. The browser receives an opaque, HttpOnly
   owner capability in `hts.trial-access`. The lifecycle worker creates or links the verified
   panel account and provisions the trial.
5. Installation completion starts the trial clock. The ready email includes the panel URL and
   the exact UTC expiry. `/trial` shows the durable state and offers a native progress refresh.
6. After 24 hours, the worker suspends the server and emails the customer. Its world/files are
   retained for another 72 hours. A confirmed paid upgrade applies the purchased resource
   limits and resumes the same server, keeping its files and identifier.
7. After retention ends, an unpaid trial is deleted through the panel API. The permanent SQL
   entitlement record remains, so deletion does not permit another trial.

The [customer area](CUSTOMER-AREA.md) uses its own short-lived verified email session.
The trial owner capability remains permission to inspect/upgrade this trial, not an administrative
panel credential or automatic access to another customer's billing.

## Trial experience and new-server credentials

New trials use the exact name `24 Teste {DisplayName}`, normalized and bounded server-side.
Their stable external ID still controls recovery/idempotence, not the display name.
Each newly created Project Zomboid server receives its own cryptographically generated
32-character `ADMIN_PASSWORD`. A static shared override is no longer necessary. Recovery of an
already-created server never rotates its credentials; Minecraft eggs are unaffected.
No password is returned to the website or written to logs. In the operator-owned PZ egg,
verify that the ADMIN_PASSWORD variable has the intended user-viewable/editable permissions
so the customer can manage permitted credentials in Pterodactyl. The application does not
change global eggs or weaken panel variable permissions.

The compact trial page distinguishes provisioning, installation, ready, expired and converted
states. Installation is not advertised as live/online game telemetry. Panel resource/address
details are displayed only when both the stored trial owner and stable external ID match.
UTC `ExpiresAt` remains authoritative: the UI shows a countdown plus Brasília and US Eastern
times, with ET/CT/MT/PT selection and automatic DST. Browser clock changes never extend the trial.

## Default limits and time windows

| Setting | Shipped value | Meaning |
| --- | --- | --- |
| `Trials__Enabled` | `false` | New requests disabled until explicitly enabled |
| `Trials__DurationHours` | `24` | Counted after installation finishes |
| `Trials__RetentionHours` | `72` | Retention after the original expiry, not after a late worker run |
| `Trials__MemoryMb` | `6144` | 6 GiB in the panel's MiB units |
| `Trials__DiskMb` | `25600` | 25 GiB in the panel's MiB units |
| `Trials__CpuPercent` | `0` | No CPU percentage limit |
| `Trials__BackupLimit` | `1` | Customer backup allowance |
| `Trials__VerificationLifetimeMinutes` | `60` | One-use email confirmation window |
| `Trials__ReservationLifetimeMinutes` | `120` | Unpaid Checkout reservation before reevaluation |
| `Trials__PollIntervalSeconds` | `30` | Worker/install polling interval |
| `Trials__RetryDelaySeconds` | `60` | Delay before retrying a failed lifecycle action |
| `Trials__LeaseMinutes` | `5` | Durable claim preventing concurrent workers from acting on one trial |

The lease must exceed `Pterodactyl__TimeoutSeconds` by more than 30 seconds. Startup validation rejects a shorter lease, including when new requests are disabled and existing trials still need maintenance.

Pterodactyl CPU `0` means unlimited CPU usage; it does not mean zero CPU. Memory/disk `0` also
mean unlimited in the upstream panel; HTS uses positive 6144/25600 values for these trial limits.
Swap `0` means disabled. These semantics are explicit in the
[official panel form](https://github.com/pterodactyl/panel/blob/1.0-develop/resources/views/admin/servers/new.blade.php).

## Required services

- Commerce SQL with the explicit `AddServerTrials` migration applied. The legacy Hosting
  database fallback is insufficient for durable trial entitlement and lifecycle state.
- Backend-only Pterodactyl **Application API** key and a configured location/egg. Read, create,
  suspend, unsuspend, build update and delete are required by this workflow.
- SMTP relay with a sender address. The service must be able to deliver verification mail
  before provisioning can start; setting a frontend flag alone cannot activate trials.
- Correct HTTPS `Site.BaseUrl`, AllowedHosts, reverse proxy forwarding and a running lifecycle
  worker. A verification email must link to the intended public site, not localhost.
- Approved Minecraft profiles and versions before offering those configurations. Project
  Zomboid can use the existing configured Pterodactyl template.

Stripe and configured paid plan prices are required for the upgrade path. Trial availability
does not imply every game/edition has a paid tier. Minecraft paid tiers are configured
separately; empty tiers must not become invented prices or a fake checkout.

## SMTP configuration

Configure credentials in the private runtime environment; never commit or print them:

| Key | Purpose |
| --- | --- |
| `Smtp__Host` | Relay hostname or IP, without a URL scheme |
| `Smtp__Port` | STARTTLS relay port; default 587 |
| `Smtp__EnableSsl` | `true` required outside Development |
| `Smtp__Username` / `Smtp__Password` | Both configured, or both omitted for an authorized relay |
| `Smtp__FromAddress` | One sender email address without a display name |
| `Smtp__FromName` | Sender display name; default HTS Hosting |
| `Smtp__DeliveryTimeoutSeconds` | Total send budget; default 15 seconds |

The transport uses .NET `SmtpClient` with STARTTLS; choose a compatible relay rather than an
implicit-TLS-only port. Messages are UTF-8 plain text and use the request's EN/PT-BR locale,
including later worker notifications. Expiry/retention dates are explicit UTC timestamps.
The transport omits raw SMTP error details from exceptions because responses may repeat a
recipient or relay detail. Delivery acceptance is not a guarantee that an inbox accepted it;
verify relay logs privately when troubleshooting.

## Minecraft profile allowlist

An enabled `Trials:Profiles` entry contains operator-owned deployment configuration:

| Field | Requirement |
| --- | --- |
| `Id` | Unique public profile identifier, at most 64 characters |
| `GameSlug` | `minecraft` |
| `Edition` | `java` or `bedrock` |
| `Variant` | Approved software label; Bedrock currently supports `vanilla` |
| `Enabled` | Explicitly `true` after operator validation |
| `NestId`, `EggId` | Existing compatible nest/egg |
| `LocationId` | Eligible configured deployment location, or the configured fallback |
| `AllowedVersions` | Explicit list of supported version strings |
| `VersionEnvironmentVariable` | The egg's actual variable receiving the selected version |
| `DockerImage`, `StartupCommand`, `Environment` | Optional operator overrides compatible with the egg |

For private env binding, use names such as `Trials__Profiles__0__Id`,
`Trials__Profiles__0__AllowedVersions__0`, and `Trials__Profiles__0__Environment__VARIABLE_NAME`.
Do not copy a made-up nest/egg or assume a version variable name: inspect the intended egg.
Java labels such as Vanilla, Paper, Forge and Fabric are available only when an enabled
approved profile exists. Bedrock is not offered Java-only Forge/Fabric software. Version,
profile and edition are validated again on the server before creating resources.

`AvailableProfiles` exposes only public selectors and version lists. API keys, image/startup
commands, environment values, node IDs and egg IDs never become browser deployment inputs.

## Ownership, recovery and payment

Verification/access tokens contain cryptographically random bytes; SQL stores their SHA-256
hashes instead of the original token. Confirmation consumes the verification token and grants
owner access. The cookie is HttpOnly, SameSite Lax, Secure over HTTPS, and lasts up to seven
days; durable state/deadline checks control actual access to upgrades, not cookie lifetime.

The separate `/trials/recover` form accepts an email and returns the same generic inbox result
for an unknown account. It creates no new entitlement or server. A verified existing owner
can recover access even when new trials are disabled or the selected Minecraft profile is
removed. A fresh confirmation rotates the owner capability without restarting the trial.

Checkout associates a paid order with the verified trial through a durable reservation. The
browser cannot choose a foreign server ID or claim ownership using an email field. The
server rechecks game, owner capability, state, deadline and current price. Webhook-confirmed
payment converts the existing server; a redirect to the success page does not authorize it.
Cancellation/expiry releases unpaid work as implemented by the checkout/lifecycle integration.
Paid or unresolved reservations prevent deletion until the durable state is reconciled.

## Persistence, retry and operations

`server_trials` permanently records normalized email entitlement and unique panel-user claims.
It also records selection, locale, token hashes, panel IDs, installation/expiry/deletion times,
order reservation, notification flags, lease/retry fields and sanitized failure class.
One-per-account is global across Project Zomboid and Minecraft. Changing games, recovering
access, deleting the server or removing its profile does not reset the entitlement.

The worker reads due records from SQL, claims a durable lease, and persists progress around
panel operations. Stable `hts-trial-…` external IDs recover creation after a lost response.
API failures retry; a failed suspension/deletion does not falsely report completion. A delete
claim enters `Deleting`, excluding new reservations before the external delete operation.
Notifications follow persisted lifecycle state and may be delivered more than once after a
crash; they never extend the entitlement or create another trial.

`Trials.Enabled=false` stops new trials while existing expiry/retention obligations continue.
Disabling the flag is not a deletion-worker pause. If maintenance requires stopping lifecycle
actions, plan an explicit operational change and reconcile deadlines, payment reservations,
panel state and SQL after recovery. Restoring a database without reconciling panel/Stripe
references can otherwise recover stale obligations or paid upgrades incorrectly.

## Source and checks

- [Trial contracts](../src/HowToSoftware.Hosting/Services/Trials/TrialContracts.cs),
  [options](../src/HowToSoftware.Hosting/Services/Trials/TrialOptions.cs) and
  [service](../src/HowToSoftware.Hosting/Services/Trials/TrialService.cs).
- [SQL store](../src/HowToSoftware.Hosting/Services/Trials/SqlServerTrialStore.cs),
  [worker](../src/HowToSoftware.Hosting/Services/Trials/TrialLifecycleWorker.cs),
  [panel gateway](../src/HowToSoftware.Hosting/Services/Trials/TrialPanelGateway.cs).
- [SMTP](../src/HowToSoftware.Hosting/Services/Trials/SmtpTrialEmailSender.cs),
  [native endpoints](../src/HowToSoftware.Hosting/Endpoints/TrialEndpoints.cs) and
  [trial page](../src/HowToSoftware.Hosting/Components/Pages/Trial.razor).
- [Explicit commerce migration](../src/HowToSoftware.Hosting/Data/CommerceMigrations/20261005170245_AddServerTrials.cs).
- [Email checks](../tests/HowToSoftware.Hosting.Tests/TrialEmailTests.cs),
  [HTTP/CSRF/owner cookie checks](../tests/HowToSoftware.Hosting.Tests/TrialEndpointTests.cs).

Automated checks use fake transports/panel seams and isolated stores. They do not constitute
production SMTP delivery, installed-egg compatibility, player connectivity or restored-backup
validation. Verify those in a controlled deployment before enabling new requests.
