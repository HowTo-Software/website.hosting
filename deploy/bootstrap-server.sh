#!/usr/bin/env bash
# One-time preparation of the production host for the CI/CD pipeline. Run it ON the server as the
# account the runner will use (htsadmin, a member of the docker group). The only root step is
# creating the deployment directory, which an administrator does once beforehand:
#
#   sudo install -d -o htsadmin -g htsadmin -m 750 /opt/howtoosoftware-hosting-prototype
#
#   RUNNER_TOKEN=<registration token> bash bootstrap-server.sh
#
# Get a registration token (valid for one hour) from a machine with repository admin rights:
#   gh api -X POST repos/HowTo-Software/howtoosoftware-hosting-prototype/actions/runners/registration-token --jq .token
#
# Options (environment variables):
#   MIGRATE_WITH_RUNTIME_LOGIN=1   use the runtime SQLSERVER_CONNECTION_STRING for migrations
#                                  instead of prompting for a dedicated migration login.
#
# Safe to re-run: every step checks before it acts, and existing files are never overwritten.

set -Eeuo pipefail

readonly REPO_URL="https://github.com/HowTo-Software/howtoosoftware-hosting-prototype"
readonly DEPLOY_DIR="/opt/howtoosoftware-hosting-prototype"
readonly LIVE_CONTAINER="hts-hosting-site"
readonly RUNNER_DIR="$HOME/actions-runner-hts-hosting"
readonly RUNNER_VERSION="2.337.0"
readonly RUNNER_SHA256="70920811a4f8ad4328818682bca5c6469c1c942fab52448868071d0063816613"
readonly RUNNER_LABELS="hts-production"
readonly RUNNER_UNIT="actions-runner-hts-hosting.service"
RUNNER_NAME="hts-production-$(hostname)"
readonly RUNNER_NAME

# Set by the image or by the compose file itself, so not part of the operator's configuration.
readonly NON_CONFIG_KEYS='^(PATH|HOME|HOSTNAME|APP_UID|ASPNET_VERSION|DOTNET_VERSION|DOTNET_RUNNING_IN_CONTAINER|DOTNET_gcServer|ASPNETCORE_HTTP_PORTS|ASPNETCORE_ENVIRONMENT|ASPNETCORE_URLS)='

log() { printf '[bootstrap] %s\n' "$*"; }
fail() { printf '[bootstrap] ERROR: %s\n' "$*" >&2; exit 1; }

user="$(id -un)"
[[ "$user" != "root" ]] || fail "Run as the runner account, not root."
id -nG "$user" | grep -qw docker || fail "$user must be in the docker group."

umask 077

# 1. Deployment directory. /opt needs root to create it, so it must already exist.
[[ -d "$DEPLOY_DIR" && -w "$DEPLOY_DIR" ]] \
  || fail "$DEPLOY_DIR must exist and be writable by $user. Ask an administrator to run: sudo install -d -o $user -g $user -m 750 $DEPLOY_DIR"
chmod 750 "$DEPLOY_DIR"

# 2. Runtime configuration, carried over from the deployment this replaces. Values are written
#    straight to the file and never echoed.
if [[ ! -f "$DEPLOY_DIR/.env" ]]; then
  if docker inspect "$LIVE_CONTAINER" >/dev/null 2>&1; then
    log "Capturing runtime configuration from the running $LIVE_CONTAINER container"
    expected="$(docker inspect --format '{{len .Config.Env}}' "$LIVE_CONTAINER")"
    docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$LIVE_CONTAINER" \
      | sed '/^$/d' > "$DEPLOY_DIR/.env.all"
    # A value spanning lines cannot be expressed in an env file; refuse rather than truncate.
    [[ "$(wc -l < "$DEPLOY_DIR/.env.all")" == "$expected" ]] \
      || { rm -f "$DEPLOY_DIR/.env.all"; fail "A configuration value contains a line break; create $DEPLOY_DIR/.env by hand."; }
    grep -Ev "$NON_CONFIG_KEYS" "$DEPLOY_DIR/.env.all" > "$DEPLOY_DIR/.env" || true
    rm -f "$DEPLOY_DIR/.env.all"
  else
    fail "No $DEPLOY_DIR/.env and no running $LIVE_CONTAINER. Create $DEPLOY_DIR/.env (mode 600) from .env.example."
  fi
  log "Wrote $DEPLOY_DIR/.env with $(grep -c '=' "$DEPLOY_DIR/.env") settings"
fi
chmod 600 "$DEPLOY_DIR/.env"
grep -q '^SQLSERVER_CONNECTION_STRING=.' "$DEPLOY_DIR/.env" \
  || fail "$DEPLOY_DIR/.env has no SQLSERVER_CONNECTION_STRING; the commerce database is required."

# 3. Migration login.
if [[ ! -f "$DEPLOY_DIR/migrate.env" ]]; then
  if [[ "${MIGRATE_WITH_RUNTIME_LOGIN:-0}" == "1" ]]; then
    log "Using the runtime login for migrations (MIGRATE_WITH_RUNTIME_LOGIN=1)"
    grep '^SQLSERVER_CONNECTION_STRING=' "$DEPLOY_DIR/.env" > "$DEPLOY_DIR/migrate.env"
  elif [[ -t 0 ]]; then
    log "Enter the SQL Server connection string for the MIGRATION login (input hidden)."
    read -r -s -p "SQLSERVER_CONNECTION_STRING: " migrate_connection
    printf '\n'
    [[ -n "$migrate_connection" ]] || fail "A migration connection string is required."
    printf 'SQLSERVER_CONNECTION_STRING=%s\n' "$migrate_connection" > "$DEPLOY_DIR/migrate.env"
    unset migrate_connection
  else
    fail "No $DEPLOY_DIR/migrate.env. Re-run interactively, or with MIGRATE_WITH_RUNTIME_LOGIN=1."
  fi
fi
chmod 600 "$DEPLOY_DIR/migrate.env"

# 4. Self-hosted GitHub Actions runner.
if [[ ! -f "$RUNNER_DIR/.runner" ]]; then
  [[ -n "${RUNNER_TOKEN:-}" ]] || fail "Set RUNNER_TOKEN to a repository registration token."
  mkdir -p "$RUNNER_DIR"
  chmod 700 "$RUNNER_DIR"
  cd "$RUNNER_DIR"
  archive="actions-runner-linux-x64-${RUNNER_VERSION}.tar.gz"
  log "Downloading runner $RUNNER_VERSION"
  curl -fsSL -o "$archive" "https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/${archive}"
  echo "${RUNNER_SHA256}  ${archive}" | sha256sum -c --quiet - || fail "Runner download failed its checksum."
  tar xzf "$archive"
  rm -f "$archive"
  ./config.sh --unattended --replace \
    --url "$REPO_URL" \
    --token "$RUNNER_TOKEN" \
    --name "$RUNNER_NAME" \
    --labels "$RUNNER_LABELS" \
    --work _work
fi

# 5. Run it as a systemd *user* service. Lingering starts the user manager at boot, so the
#    runner survives logouts and reboots without a system-wide unit (and without root).
if [[ "$(loginctl show-user "$user" -p Linger --value 2>/dev/null)" != "yes" ]]; then
  loginctl enable-linger "$user" || fail "Could not enable lingering; ask an administrator to run: sudo loginctl enable-linger $user"
fi

mkdir -p "$HOME/.config/systemd/user"
cat > "$HOME/.config/systemd/user/$RUNNER_UNIT" <<UNIT
[Unit]
Description=GitHub Actions runner for howtoosoftware-hosting-prototype ($RUNNER_LABELS)
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=$RUNNER_DIR
ExecStart=$RUNNER_DIR/run.sh
Restart=always
RestartSec=10
KillMode=process
KillSignal=SIGTERM
TimeoutStopSec=5min

[Install]
WantedBy=default.target
UNIT
chmod 644 "$HOME/.config/systemd/user/$RUNNER_UNIT"

systemctl --user daemon-reload
systemctl --user enable --now "$RUNNER_UNIT" >/dev/null
sleep 3
systemctl --user is-active --quiet "$RUNNER_UNIT" || fail "Runner service did not start: systemctl --user status $RUNNER_UNIT"

log "Done. $DEPLOY_DIR is ready and runner '$RUNNER_NAME' ($RUNNER_LABELS) is running as $RUNNER_UNIT."
