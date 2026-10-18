# Production deployment and CI/CD

> **Status:** Procedure checked against scripts; external infrastructure unverified
>
> **Owner:** HTS / HowToSoftware maintainers
>
> **Last updated:** 2026-10-18

[Index](README.md) · [Pipeline](phase-5-deployment/ci-cd.md) · [Runbook](phase-6-operations/runbook.md) · [Configuration](phase-3-development/configuration.md)

The code defines Docker image deployment through GitHub Actions. PRs run dependency auditing, build/tests, image build and isolated runtime smoke checks; a push to main publishes an image and enters the production Environment after these checks pass. Reviewer approval depends on GitHub Settings, not just YAML.

The previous guide recorded host `192.168.1.206`, user `htsadmin`, and URL `https://host.howto.software`. These are deployment references; the documentation review **did not verify host state, DNS, credentials, runner, or approval rules**.

## Files and artifacts

| Piece | Source |
| --- | --- |
| Pipeline | [.github/workflows/ci-cd.yml](../.github/workflows/ci-cd.yml) |
| Deployment script | [deploy/deploy.sh](../deploy/deploy.sh) |
| Production Compose | [deploy/docker-compose.production.yml](../deploy/docker-compose.production.yml) |
| Bootstrap | [deploy/bootstrap-server.sh](../deploy/bootstrap-server.sh) |
| Runtime smoke checks | [deploy/smoke-test.ps1](../deploy/smoke-test.ps1) |
| Image | ghcr.io/howto-software/howtoosoftware-hosting-prototype:sha-commit |
| Container | hts-hosting-site |
| Host → container port | 5147 → 8080 |
| Key volume | hts-hosting-prototype_dataprotection-keys |

## Preconditions

- Runner account with Docker access, deployment directory access, and bootstrap dependencies.
- Runtime configured with correct domain, hosts/proxy, Stripe, SQL, and Pterodactyl.
- Existing `.env` and `migrate.env`, mode 600, outside Git.
- Available commerce SQL and migration compatibility with the previous image.
- Recoverable backups and separate migration/runtime credentials where applicable.
- GitHub `production` Environment with suitable reviewers/branches; the runner must not execute PRs.
- Docker Compose 2.30.0 or newer, supporting `env_file.format: raw`.

The versioned host allowlist includes `host.howto.software`, the older public domains and
loopback health checks. Both Compose files preserve `APP_ALLOWED_HOSTS` from their private
raw env file rather than replacing it with a hardcoded list. Set it to the intended public
hostname plus `localhost;127.0.0.1;[::1]`; include a LAN IP only when that access is needed.
If the variable is already present in the server's private `.env`, update its value there
before deployment: changing the repository default does not override an explicit value.
Set `APP_BASE_URL=https://host.howto.software` (or the actual public origin) separately for
canonical links, email links and payment returns. Static asset URLs stay same-origin.

Configure `Security__KnownProxies__0` with the address of the immediate reverse proxy as
seen by the container. The proxy must preserve the allowed Host and provide matching
`X-Forwarded-For` and `X-Forwarded-Proto` headers. Unknown peers cannot enable HTTPS through
headers. A trusted HTTPS request receives Secure antiforgery cookies, HSTS and a CSP with
`upgrade-insecure-requests`; direct HTTP receives no browser-side scheme upgrade.

## Runtime verification before deployment

The CI image job loads the built image, starts it with the production environment, nonroot
user, read-only filesystem and no integration credentials, then runs `smoke-test.ps1`.
The check covers repeated uncached homepage requests, the configured public hostname,
public routes, fingerprinted JS/CSS/fonts, HTTP and trusted HTTPS transport policy, and a
real antiforgery cookie/token form round trip. A POST without its cookie must still fail.
An image or smoke failure blocks the deploy job. Pull requests never run on production.

To repeat the request checks against an **isolated, unconfigured** published application:

```powershell
./deploy/smoke-test.ps1 -BaseUrl http://127.0.0.1:15147
```

Add `-CheckForwardedHttps` only when the test peer is an explicitly trusted proxy. Do not
run this fixture against a configured live service: it intentionally posts a synthetic
trial recovery form and expects the disabled integration result. This verifies runtime
packaging and request boundaries, not actual mail delivery, SQL migrations, a real Stripe
charge or a Pterodactyl deployment. Validate those services with controlled staging resources
and the integration-specific guides before enabling commerce or new trials.

## Expected layout

```text
/opt/howtoosoftware-hosting-prototype/    operational owner; mode 750
  .env                                 runtime configuration; mode 600
  migrate.env                          migration SQL connection; mode 600
  docker-compose.yml                   copy of deploy/
  image.env                            deployed HTS_IMAGE
```

The default directory is `/opt/howtoosoftware-hosting-prototype`; `HTS_DEPLOY_DIR` can choose another target. Do not use a personal directory for this stack without adapting the procedure.

Production env files contain literal KEY=value lines, without outer quotes or expansion. Raw format prevents Compose from interpreting password characters. Do not print their contents to verify deployment.

## What deploy.sh does

1. Validates image repository/tag or digest and private-file permissions.
2. Pulls the GHCR image; the job uses isolated Docker configuration and a temporary token.
3. Runs a temporary container with `migrate.env` and `--migrate-commerce`.
4. If migration fails, leaves the running container unchanged.
5. Records `image.env` and runs Compose to replace the application.
6. Queries health at `127.0.0.1:5147`, up to 15 attempts with 6-second waits, plus individual request durations.
7. If health fails, attempts to restore the previous image and fails the job even if recovery succeeds.
8. After success, cleans old images within repository/label constraints, not global volumes or databases.

Rollback does not reverse migrations. Use compatible evolution: add first, migrate consumers, remove only in a later version.

The runtime image uses .NET 10, a nonroot user, read-only filesystem, temporary /tmp, and a Data Protection volume. These do not replace firewall, TLS, or restricted host access.

## Prepare or rebuild the host

An administrator must create the directory with appropriate ownership/group:

```bash
sudo install -d -o htsadmin -g htsadmin -m 750 /opt/howtoosoftware-hosting-prototype
```

Run `deploy/bootstrap-server.sh` interactively as the operational account, providing a runner registration token privately. It checks the directory, can capture existing container configuration without displaying values, prepares files, and installs the runner user service. Existing env files are not overwritten.

`MIGRATE_WITH_RUNTIME_LOGIN=1` permits runtime-login reuse but does not prove production uses it. Prefer a separate DDL principal, as described in [SQLSERVER-SETUP.md](SQLSERVER-SETUP.md).

Verify runner labels, service, lingering, package repository association, reviewers, and branch restrictions. An earlier setup record does not mean everything remains configured.

## Deploy, redeploy, and restore an image

- Deploy: complete PR, merge into main, and meet Environment approval requirements when configured.
- Redeploy main: manual workflow with an empty tag.
- Restore image: manual workflow on main, `image_tag` set to an existing tag from the same repository.
- Inspect results: effective image, private logs, and health, then appropriate route/integration tests.

An existing tag skips current-tree build/tests. Choose an image compatible with the database; do not use mutable `main` as a rollback identifier.

## Roll out automatic trials and public announcements

Keep `Trials.Enabled=false` during the first rollout. Apply the commerce `AddServerTrials`
migration through the existing explicit deployment/migration path, then verify the runtime
SQL principal can use `server_trials` without granting it DDL permissions. A legacy Hosting
database alone does not implement trial entitlement or expiration.

Configure the private runtime SMTP relay and sender, the intended public `Site.BaseUrl`, and
Pterodactyl Application permissions for user lookup/create and server read/create/build,
suspend/unsuspend/delete. Configure approved Minecraft edition/software/version profiles
before enabling their selection. Empty paid Minecraft tiers must remain unpriced. Before
publishing paid Minecraft tiers, create matching game/plan catalog rows in commerce SQL;
environment configuration and `AddServerTrials` do not seed those records. Production must
not run the Development-only seed command. See [SQLSERVER-SETUP.md](SQLSERVER-SETUP.md).

Verify the email confirmation GET/POST boundary, SMTP delivery, installation and trial clock,
owner recovery, same-server paid conversion, expiry suspension and retention deletion against
controlled test accounts/resources. The normal unit suite uses isolated seams and does not
validate a real relay or production egg. See [TRIAL-SERVERS.md](TRIAL-SERVERS.md).

The lifecycle worker retries due SQL records and continues existing obligations even when
new trials are disabled. `Trials.Enabled=false` is not a pause for already-created trials.
Plan maintenance/rollback with the durable schema, retention deadlines and paid reservations
in mind; reconcile SQL with Stripe/panel state after restoring a backup.

Public promotion announcements default to an empty allowlist. Opt in only existing codes
approved for public display; announcement configuration creates no promotion or discount and
does not list private coupons. See [PUBLIC-PROMOTIONS.md](PUBLIC-PROMOTIONS.md).

## Root Compose files

Two local files have different defaults:

| File | Service / port | Purpose |
| --- | --- | --- |
| docker-compose.yml | site / 5147 | Local build with .env; not the raw deployment Compose |
| compose.yaml | web / 5003 | Older container alternative; incomplete commerce configuration |
| deploy/docker-compose.production.yml | site / 5147 | Official deployment source |

Specify `-f` explicitly to avoid starting the wrong composition. The root stack does not include SQL Server or Pterodactyl services.

See [deployment acceptance](phase-5-deployment/deployment-guide.md), [monitoring](phase-6-operations/monitoring.md), and [recovery](phase-6-operations/disaster-recovery.md).
