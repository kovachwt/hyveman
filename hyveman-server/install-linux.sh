#!/usr/bin/env bash
#
# Hyveman server installer for Linux — runs as a per-user systemd service
# (no root required). Counterpart of install.ps1 (Windows service).
#
# What it does (idempotent — re-running updates the binary and preserves data):
#   1. dotnet publish (linux-x64, self-contained single file) into ~/.local/lib/hyveman/server
#   2. create the data dir skeleton (default ~/.local/share/hyveman/server)
#   3. generate a self-signed TLS cert (config/cert.pfx) if none is configured
#   4. write a default config/server.json (never overwrites an existing one)
#   5. install ~/.config/systemd/user/hyveman-server.service and enable+start it
#   6. attempt `loginctl enable-linger` so the service survives logout
#
# Run with -h/--help for options (or read USAGE below).

set -euo pipefail

SERVICE_NAME="hyveman-server"
UNIT_FILE="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/${SERVICE_NAME}.service"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVER_PROJ="$REPO_ROOT/hyveman-server/Hyveman.Server.csproj"

USAGE="Usage: $0 [options]

  -p, --port PORT      HTTPS listen port (default: 8443; ignored if server.json exists)
  --data-dir DIR       data directory (default: \$XDG_DATA_HOME/hyveman/server or
                       ~/.local/share/hyveman/server)
  --install-dir DIR    directory for the binary (default: ~/.local/lib/hyveman/server)
  --cert PATH          use an existing TLS cert (PFX or PEM) instead of generating one
  --exe PATH           use a pre-published hyveman-server binary instead of dotnet publish
  --no-publish         skip publish; use whatever is already in --install-dir
  --no-start           install/update but do not start or restart the service
  --uninstall          stop + disable + remove the service unit and the binary
                       (the data dir and all data are preserved)
  -h, --help

Env overrides: HYVEMAN_PORT, HYVEMAN_DATA_DIR, HYVEMAN_INSTALL_DIR
"

# ── defaults / arg parsing ──────────────────────────────────────────────────
PORT="${HYVEMAN_PORT:-8443}"
DATA_DIR="${HYVEMAN_DATA_DIR:-}"
INSTALL_DIR="${HYVEMAN_INSTALL_DIR:-}"
CERT_PATH=""
EXE_PATH=""
NO_PUBLISH=0
NO_START=0
UNINSTALL=0

usage() { printf '%s' "$USAGE"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        -p|--port) PORT="${2:?--port needs a value}"; shift 2 ;;
        --data-dir) DATA_DIR="${2:?--data-dir needs a value}"; shift 2 ;;
        --install-dir) INSTALL_DIR="${2:?--install-dir needs a value}"; shift 2 ;;
        --cert) CERT_PATH="${2:?--cert needs a value}"; shift 2 ;;
        --exe) EXE_PATH="${2:?--exe needs a value}"; shift 2 ;;
        --no-publish) NO_PUBLISH=1; shift ;;
        --no-start) NO_START=1; shift ;;
        --uninstall) UNINSTALL=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "error: unknown option '$1'" >&2; usage >&2; exit 1 ;;
    esac
done

if [[ -z "$DATA_DIR" ]]; then
    XDG="${XDG_DATA_HOME:-}"
    if [[ -z "$XDG" ]]; then XDG="$HOME/.local/share"; fi
    DATA_DIR="$XDG/hyveman/server"
fi
if [[ -z "$INSTALL_DIR" ]]; then
    INSTALL_DIR="$HOME/.local/lib/hyveman/server"
fi
INSTALL_DIR="$(realpath -m "$INSTALL_DIR")"
DATA_DIR="$(realpath -m "$DATA_DIR")"

die() { echo "error: $*" >&2; exit 1; }
say() { echo "==> $*"; }

# ── uninstall ────────────────────────────────────────────────────────────────
if [[ "$UNINSTALL" -eq 1 ]]; then
    say "Stopping and removing user service $SERVICE_NAME (data dir preserved)..."
    systemctl --user disable --now "$SERVICE_NAME" 2>/dev/null || true
    rm -f "$UNIT_FILE"
    systemctl --user daemon-reload 2>/dev/null || true
    if [[ -d "$INSTALL_DIR" ]]; then
        rm -rf "$INSTALL_DIR"
        say "Removed install dir: $INSTALL_DIR"
    fi
    say "Done. Data dir kept: $DATA_DIR"
    exit 0
fi

# ── preflight ────────────────────────────────────────────────────────────────
if ! command -v systemctl >/dev/null 2>&1; then
    die "systemctl not found — this installer targets systemd-based Linux."
fi
if ! systemctl --user >/dev/null 2>&1; then
    die "no systemd user session available (systemctl --user failed)."
fi
if [[ "$NO_PUBLISH" -eq 0 && -z "$EXE_PATH" ]] && ! command -v dotnet >/dev/null 2>&1; then
    die "dotnet SDK not found — either install it or pass --exe <path> with a pre-published binary."
fi
if [[ -z "$EXE_PATH" && "$NO_PUBLISH" -eq 0 && ! -f "$SERVER_PROJ" ]]; then
    die "server project not found at $SERVER_PROJ (installer must run from the repo)."
fi

mkdir -p "$INSTALL_DIR" "$DATA_DIR/config" "$DATA_DIR/backup/daily" \
    "$DATA_DIR/backup/weekly" "$DATA_DIR/backup/monthly" "$DATA_DIR/logs" "$DATA_DIR/state"
chmod 700 "$DATA_DIR" "$DATA_DIR/config" "$DATA_DIR/backup" "$DATA_DIR/backup/daily" \
    "$DATA_DIR/backup/weekly" "$DATA_DIR/backup/monthly" "$DATA_DIR/logs" "$DATA_DIR/state"

# ── binary ───────────────────────────────────────────────────────────────────
if [[ -n "$EXE_PATH" ]]; then
    [[ -f "$EXE_PATH" ]] || die "--exe path not found: $EXE_PATH"
    say "Copying pre-published binary $EXE_PATH"
    cp -f "$EXE_PATH" "$INSTALL_DIR/hyveman-server"
    chmod 755 "$INSTALL_DIR/hyveman-server"
elif [[ "$NO_PUBLISH" -eq 0 ]]; then
    say "Publishing (linux-x64, self-contained single file) — first run restores packages..."
    dotnet publish "$SERVER_PROJ" -c Release -r linux-x64 --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -o "$INSTALL_DIR" 2>&1 | tail -n 3
    [[ -x "$INSTALL_DIR/hyveman-server" ]] || die "publish did not produce $INSTALL_DIR/hyveman-server"
else
    [[ -x "$INSTALL_DIR/hyveman-server" ]] || die "--no-publish but no binary at $INSTALL_DIR/hyveman-server"
fi

# ── TLS certificate ────────────────────────────────────────────────────────────
CONFIG_FILE="$DATA_DIR/config/server.json"
CERT_FILE="$DATA_DIR/config/cert.pfx"

CONF_CERT=""
if [[ -f "$CONFIG_FILE" ]]; then
    CONF_CERT="$(grep -oP '"cert_path"\s*:\s*"\K[^"]+' "$CONFIG_FILE" 2>/dev/null | head -1 || true)"
fi
# We need a cert when there is no config, or the existing config has none (an
# operator config without tls.cert_path cannot start on Linux — OptionsResolver
# rejects it), and no cert file is already in place.
NEED_CERT=0
if [[ ! -f "$CONFIG_FILE" ]]; then
    NEED_CERT=1
elif [[ -z "$CONF_CERT" && ! -f "$CERT_FILE" ]]; then
    NEED_CERT=1
fi

if [[ "$NEED_CERT" -eq 1 ]]; then
    if [[ -n "$CERT_PATH" ]]; then
        [[ -f "$CERT_PATH" ]] || die "--cert path not found: $CERT_PATH"
        say "Using provided certificate $CERT_PATH"
        cp -f "$CERT_PATH" "$CERT_FILE"
        chmod 600 "$CERT_FILE"
    elif [[ -f "$CERT_FILE" ]]; then
        say "Using existing certificate $CERT_FILE"
    else
        command -v openssl >/dev/null 2>&1 || die "openssl not found and no --cert given — cannot generate a TLS cert."
        HOSTNAME_SHORT="$(hostname -s 2>/dev/null || hostname)"
        say "Generating self-signed TLS certificate (CN=$HOSTNAME_SHORT, SAN: localhost + hostname)..."
        TMP_CERT="$(mktemp -d)"
        trap 'rm -rf "$TMP_CERT"' EXIT
        openssl req -x509 -newkey rsa:2048 -sha256 -days 825 -nodes \
            -keyout "$TMP_CERT/key.pem" -out "$TMP_CERT/cert.pem" \
            -subj "/CN=$HOSTNAME_SHORT" \
            -addext "subjectAltName=DNS:localhost,DNS:$HOSTNAME_SHORT,IP:127.0.0.1" 2>/dev/null
        openssl pkcs12 -export -out "$CERT_FILE" -inkey "$TMP_CERT/key.pem" \
            -in "$TMP_CERT/cert.pem" -passout pass: 2>/dev/null
        chmod 600 "$CERT_FILE"
        trap - EXIT
        rm -rf "$TMP_CERT"
        say "Wrote $CERT_FILE (self-signed, empty password — pin it on agents or install your CA)."
    fi
fi

# ── default config (never overwrite a functional operator config) ────────────
write_default_config() {
    say "Writing default $CONFIG_FILE"
    cat > "$CONFIG_FILE" <<EOF
{
  "urls": "https://0.0.0.0:$PORT",
  "tls": {
    "cert_path": "config/cert.pfx",
    "cert_password": "",
    "min_tls": "1.2",
    "preferred_tls": "1.3"
  },
  "ingest": {
    "max_batch_bytes": 4194304,
    "max_items": 1000,
    "max_raw_bytes": 16384,
    "max_message_bytes": 65536,
    "max_field_bytes": 65536,
    "max_record_id_len": 128,
    "per_source_rate": { "requests_per_min": 120, "bytes_per_min": 33554432 },
    "global_rate": { "requests_per_min": 1200 }
  },
  "poller": { "interval_s": 60, "timeout_s": 15, "concurrency": 4 },
  "alerts": { "sweep_s": 10, "default_heartbeat_miss_s": 180 },
  "notifications": { "webhook": { "allow_private": false, "allowed_hosts": [] } },
  "retention": {
    "events_days": 365,
    "metrics_days": 365,
    "health_snapshots_days": 365,
    "audit_days": 730,
    "resolved_alerts_days": 730,
    "vacuum_after_purge": true
  },
  "backup": { "time_local": "03:00", "keep_daily": 7, "keep_weekly": 4, "keep_monthly": 12 },
  "web": { "session_days": 14 },
  "logging": { "level": "Information", "file_retain_days": 14 }
}
EOF
    chmod 600 "$CONFIG_FILE"
}

if [[ ! -f "$CONFIG_FILE" ]]; then
    write_default_config
elif [[ -z "$CONF_CERT" && -f "$CERT_FILE" ]]; then
    # Config exists but has no usable cert and we now have one — heal it, keeping a backup.
    BACKUP="$CONFIG_FILE.bak.$(date +%Y%m%d%H%M%S)"
    say "Config $CONFIG_FILE has no tls.cert_path; backing it up to $BACKUP and writing defaults."
    cp -f "$CONFIG_FILE" "$BACKUP"
    write_default_config
else
    say "Config exists — preserving $CONFIG_FILE"
    if [[ -n "$CONF_CERT" && ! -f "$CONF_CERT" && ! -f "$DATA_DIR/$CONF_CERT" ]]; then
        echo "    warning: config references cert '$CONF_CERT' which is missing — the server"
        echo "    will refuse to start until you provide it (--cert PATH) or fix tls.cert_path."
    fi
fi

# ── systemd user unit ────────────────────────────────────────────────────────
mkdir -p "$(dirname "$UNIT_FILE")"
say "Writing $UNIT_FILE"
cat > "$UNIT_FILE" <<EOF
[Unit]
Description=Hyveman Server (ingest API, hardware poller, alerts, Blazor UI)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
# Content root defaults to CWD; pin it to the install dir so wwwroot/static
# assets resolve deterministically regardless of where systemd launches us.
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/hyveman-server --data-dir $DATA_DIR
Environment=HYVEMAN_DATA_DIR=$DATA_DIR
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=on-failure
RestartSec=5
UMask=0077

# Hardening: the server only needs its own dirs + network.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=$DATA_DIR $INSTALL_DIR
RestrictSUIDSGID=true
ProtectKernelTunables=true
ProtectControlGroups=true

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload

# ── enable + start ───────────────────────────────────────────────────────────
if [[ "$NO_START" -eq 0 ]]; then
    say "Enabling and starting $SERVICE_NAME..."
    systemctl --user enable --now "$SERVICE_NAME" >/dev/null 2>&1 || true
    systemctl --user restart "$SERVICE_NAME"
    # Linger keeps the service running after logout (best-effort; may need polkit).
    if ! loginctl enable-linger "$USER" 2>/dev/null; then
        echo "    note: could not enable linger (may need: sudo loginctl enable-linger $USER)."
        echo "    The service will stop when you log out unless linger is enabled."
    fi
else
    say "Enabling $SERVICE_NAME (not started)..."
    systemctl --user enable "$SERVICE_NAME" >/dev/null 2>&1 || true
fi

# ── health check ─────────────────────────────────────────────────────────────
if [[ "$NO_START" -eq 0 ]] && command -v curl >/dev/null 2>&1; then
    EFFECTIVE_PORT="$PORT"
    if [[ -f "$CONFIG_FILE" ]]; then
        CONF_PORT="$(grep -oP '"urls"\s*:\s*"https://[^":]+:\K[0-9]+' "$CONFIG_FILE" | head -1 || true)"
        [[ -n "$CONF_PORT" ]] && EFFECTIVE_PORT="$CONF_PORT"
    fi
    say "Health check: https://127.0.0.1:$EFFECTIVE_PORT/health"
    for _ in $(seq 1 30); do
        CODE="$(curl -ksS -o /dev/null -w '%{http_code}' \
            "https://127.0.0.1:$EFFECTIVE_PORT/health" -H 'X-Hyveman-Protocol: 1' 2>/dev/null || true)"
        if [[ "$CODE" == "200" ]]; then
            echo "    OK — server is up (HTTP $CODE)."
            break
        fi
        sleep 1
    done
    if [[ "$CODE" != "200" ]]; then
        echo "    warning: server did not answer 200 yet (last: ${CODE:-no response})."
        echo "    Check: systemctl --user status $SERVICE_NAME; journalctl --user -u $SERVICE_NAME"
    fi
fi

# ── summary ──────────────────────────────────────────────────────────────────
echo
echo "Hyveman server installed (user service)."
echo "  Binary:    $INSTALL_DIR/hyveman-server"
echo "  Data dir:  $DATA_DIR"
echo "  Config:    $CONFIG_FILE"
echo "  Service:   systemctl --user status $SERVICE_NAME"
echo "  Logs:      journalctl --user -u $SERVICE_NAME -f"
if [[ "$NO_START" -eq 0 ]]; then
    echo "  UI:        https://localhost:${EFFECTIVE_PORT:-$PORT}/  (first-run passkey wizard)"
    echo "  Health:    curl -k https://localhost:${EFFECTIVE_PORT:-$PORT}/health -H 'X-Hyveman-Protocol: 1'"
fi
echo "  Next:      browse to the UI from this machine (localhost), register a passkey,"
echo "             then add hosts/channels in /admin."
