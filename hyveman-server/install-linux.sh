#!/usr/bin/env bash
#
# Hyveman server installer for Linux. Two modes:
#   default — per-user systemd service (no root required)
#   --system — system-wide service (root; /etc/systemd/system, dedicated 'hyveman'
#              system user, /opt + /var/lib defaults, can bind ports 80/443)
# Counterpart of install.ps1 (Windows service).
#
# What it does (idempotent — re-running updates the binary and preserves data):
#   1. dotnet publish (linux-x64, self-contained single file) into the install dir
#      (~/.local/lib/hyveman/server, or /opt/hyveman/server with --system)
#   2. create the data dir skeleton (default ~/.local/share/hyveman/server, or
#      /var/lib/hyveman/server with --system)
#   3. TLS: generate a self-signed cert (config/cert.pfx), use --cert PATH, or let
#      Let's Encrypt provision automatically (--lets-encrypt EMAIL --domain NAME)
#   4. write a default config/server.json (never overwrites an existing one)
#   5. install the systemd unit (user: ~/.config/systemd/user/, or /etc/systemd/system/
#      with --system) and enable+start it
#   6. user mode: attempt `loginctl enable-linger` so the service survives logout
#
# Run with -h/--help for options (or read USAGE below).

set -euo pipefail

SERVICE_NAME="hyveman-server"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVER_PROJ="$REPO_ROOT/hyveman-server/Hyveman.Server.csproj"

USAGE="Usage: $0 [options]

Modes:
  (default)             per-user systemd service — no root, ~/.local paths, port 8443
  --system              system-wide service — requires root; /etc/systemd/system, dedicated
                        'hyveman' system user, /opt + /var/lib defaults, port 443, and can
                        bind port 80 (Let's Encrypt http-01) without a reverse proxy

Options:
  -p, --port PORT      HTTPS listen port (default: 8443 user / 443 system; ignored if server.json exists)
  --data-dir DIR       data directory (default: ~/.local/share/hyveman/server, or
                       /var/lib/hyveman/server with --system)
  --install-dir DIR    directory for the binary (default: ~/.local/lib/hyveman/server, or
                       /opt/hyveman/server with --system)
  --cert PATH          use an existing TLS cert (PFX or PEM) instead of generating one
  --lets-encrypt EMAIL enable automatic Let's Encrypt certificates (ACME http-01) with
                       this contact email; requires at least one --domain and inbound
                       access to port 80 on the public IP (see --http-port)
  --domain DOMAIN      public DNS name to put on the certificate (repeatable; required
                       with --lets-encrypt)
  --http-port PORT     port for the http-01 challenge listener (default: 80; must be
                       reachable from the internet on the server's public IP)
  --exe PATH           use a pre-published hyveman-server binary instead of dotnet publish
  --no-publish         skip publish; use whatever is already in --install-dir
  --no-start           install/update but do not start or restart the service
  --uninstall          stop + disable + remove the service unit and the binary
                       (the data dir and all data are preserved)
  -h, --help

Env overrides: HYVEMAN_PORT, HYVEMAN_DATA_DIR, HYVEMAN_INSTALL_DIR
"

# ── defaults / arg parsing ──────────────────────────────────────────────────
PORT="${HYVEMAN_PORT:-}"
DATA_DIR="${HYVEMAN_DATA_DIR:-}"
INSTALL_DIR="${HYVEMAN_INSTALL_DIR:-}"
SYSTEM=0
CERT_PATH=""
EXE_PATH=""
LE_EMAIL=""
LE_DOMAINS=()
HTTP_PORT=""
NO_PUBLISH=0
NO_START=0
UNINSTALL=0

usage() { printf '%s' "$USAGE"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        -p|--port) PORT="${2:?--port needs a value}"; shift 2 ;;
        --system) SYSTEM=1; shift ;;
        --data-dir) DATA_DIR="${2:?--data-dir needs a value}"; shift 2 ;;
        --install-dir) INSTALL_DIR="${2:?--install-dir needs a value}"; shift 2 ;;
        --cert) CERT_PATH="${2:?--cert needs a value}"; shift 2 ;;
        --lets-encrypt) LE_EMAIL="${2:?--lets-encrypt needs a value}"; shift 2 ;;
        --domain) LE_DOMAINS+=("${2:?--domain needs a value}"); shift 2 ;;
        --http-port) HTTP_PORT="${2:?--http-port needs a value}"; shift 2 ;;
        --exe) EXE_PATH="${2:?--exe needs a value}"; shift 2 ;;
        --no-publish) NO_PUBLISH=1; shift ;;
        --no-start) NO_START=1; shift ;;
        --uninstall) UNINSTALL=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "error: unknown option '$1'" >&2; usage >&2; exit 1 ;;
    esac
done

die() { echo "error: $*" >&2; exit 1; }
say() { echo "==> $*"; }

# ── mode-aware defaults ───────────────────────────────────────────────────────
# System mode: root-owned /opt + /var/lib, port 443, unit in /etc/systemd/system.
# User mode: ~/.local paths, port 8443 (user services cannot bind <1024), user unit.
if [[ "$SYSTEM" -eq 1 ]]; then
    [[ -z "$DATA_DIR" ]] && DATA_DIR="/var/lib/hyveman/server"
    [[ -z "$INSTALL_DIR" ]] && INSTALL_DIR="/opt/hyveman/server"
    [[ -z "$PORT" ]] && PORT=443
    UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
    SERVICE_USER="hyveman"
    SYSCTL="systemctl"
    JRNL="journalctl -u $SERVICE_NAME -f"
    WANTED_BY="multi-user.target"
else
    if [[ -z "${HOME:-}" ]]; then
        die "HOME is not set — cannot resolve per-user paths (use --system, or export HOME)."
    fi
    [[ -z "$DATA_DIR" ]] && DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/hyveman/server"
    [[ -z "$INSTALL_DIR" ]] && INSTALL_DIR="$HOME/.local/lib/hyveman/server"
    [[ -z "$PORT" ]] && PORT=8443
    UNIT_FILE="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/${SERVICE_NAME}.service"
    SERVICE_USER=""
    SYSCTL="systemctl --user"
    JRNL="journalctl --user -u $SERVICE_NAME -f"
    WANTED_BY="default.target"
fi
INSTALL_DIR="$(realpath -m "$INSTALL_DIR")"
DATA_DIR="$(realpath -m "$DATA_DIR")"

# ── path safety ────────────────────────────────────────────────────────────────
# Recursive ops below (chmod/chown -R, uninstall rm -rf) must never leave the
# hyveman tree: refuse the filesystem root, well-known system directories, and
# the user's home directory itself, and refuse install == data dir.
SYSTEM_DIRS="/bin /boot /dev /etc /home /lib /lib64 /media /mnt /opt /proc /root /run /sbin /srv /sys /tmp /usr /var"
HOME_REAL="$(realpath -m "${HOME:-/nonexistent}")"
for d in "$INSTALL_DIR" "$DATA_DIR"; do
    case "$d" in
        /|//) die "refusing to use '$d' as a hyveman directory." ;;
    esac
    for s in $SYSTEM_DIRS; do
        if [[ "$d" == "$s" || "$d" == "$s/" ]]; then
            die "refusing to use '$d' as a hyveman directory (system directory)."
        fi
    done
    if [[ "$d" == "$HOME_REAL" || "$d" == "$HOME_REAL/" ]]; then
        die "refusing to use your home directory as a hyveman directory."
    fi
done
[[ "$INSTALL_DIR" == "$DATA_DIR" ]] && die "--install-dir and --data-dir must be different directories."

if [[ -n "$LE_EMAIL" && "${#LE_DOMAINS[@]}" -eq 0 ]]; then
    die "--lets-encrypt requires at least one --domain (public DNS names for the certificate)."
fi
if [[ -n "$CERT_PATH" && -n "$LE_EMAIL" ]]; then
    die "--cert and --lets-encrypt are mutually exclusive."
fi
if [[ -n "$HTTP_PORT" && -z "$LE_EMAIL" ]]; then
    die "--http-port only applies with --lets-encrypt."
fi
if [[ -n "$LE_EMAIL" && "$(id -u)" -ne 0 && "${HTTP_PORT:-80}" -lt 1024 ]]; then
    echo "    note: challenge port ${HTTP_PORT:-80} is privileged and this per-user service cannot"
    echo "    bind it. Either --http-port <high port> + forward /.well-known/acme-challenge/"
    echo "    from port 80 (reverse proxy), or install with --system (root; binds port 80 directly)."
fi

# ── uninstall ────────────────────────────────────────────────────────────────
if [[ "$UNINSTALL" -eq 1 ]]; then
    if [[ "$SYSTEM" -eq 1 ]]; then
        [[ "$(id -u)" -eq 0 ]] || die "--system --uninstall must run as root (try: sudo $0 --system --uninstall)"
        say "Stopping and removing system service $SERVICE_NAME (data dir preserved)..."
        systemctl disable --now "$SERVICE_NAME" 2>/dev/null || true
        rm -f "$UNIT_FILE"
        systemctl daemon-reload 2>/dev/null || true
        # Remove the dedicated service account (files keep their uid; a later reinstall
        # re-creates the account and re-chowns the data dir).
        userdel hyveman 2>/dev/null || true
    else
        say "Stopping and removing user service $SERVICE_NAME (data dir preserved)..."
        systemctl --user disable --now "$SERVICE_NAME" 2>/dev/null || true
        rm -f "$UNIT_FILE"
        systemctl --user daemon-reload 2>/dev/null || true
    fi
    if [[ -d "$INSTALL_DIR" ]]; then
        # Never delete a directory we can't prove is ours (a typo'd --install-dir
        # or a stale HYVEMAN_INSTALL_DIR must not nuke an unrelated tree).
        [[ -f "$INSTALL_DIR/hyveman-server" ]] \
            || die "refusing to remove '$INSTALL_DIR': no hyveman-server binary inside (wrong --install-dir?)"
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
if [[ "$SYSTEM" -eq 1 ]]; then
    if [[ "$(id -u)" -ne 0 ]]; then
        die "--system installs a system-wide service and must run as root (try: sudo $0 --system ...)"
    fi
    systemctl list-unit-files >/dev/null 2>&1 || die "systemd (system manager) is not reachable."
else
    if ! systemctl --user >/dev/null 2>&1; then
        die "no systemd user session available (systemctl --user failed)."
    fi
fi
if [[ "$NO_PUBLISH" -eq 0 && -z "$EXE_PATH" ]] && ! command -v dotnet >/dev/null 2>&1; then
    die "dotnet SDK not found — either install it or pass --exe <path> with a pre-published binary."
fi
if [[ -z "$EXE_PATH" && "$NO_PUBLISH" -eq 0 && ! -f "$SERVER_PROJ" ]]; then
    die "server project not found at $SERVER_PROJ (installer must run from the repo)."
fi

# ── service account (system mode only) ───────────────────────────────────────
# The system service runs as a dedicated unprivileged 'hyveman' account (with
# CAP_NET_BIND_SERVICE so it can still bind ports 80/443); root only owns the
# binary and the unit file.
if [[ "$SYSTEM" -eq 1 ]] && ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    command -v useradd >/dev/null 2>&1 || die "useradd not found — cannot create the '$SERVICE_USER' service account."
    NOLOGIN="$(command -v nologin || echo /usr/sbin/nologin)"
    useradd --system --no-create-home --home-dir /nonexistent --shell "$NOLOGIN" "$SERVICE_USER" \
        || die "could not create system user '$SERVICE_USER'."
    say "Created system user '$SERVICE_USER' (unprivileged service account)."
fi

mkdir -p "$INSTALL_DIR" "$DATA_DIR/config" "$DATA_DIR/backup/daily" \
    "$DATA_DIR/backup/weekly" "$DATA_DIR/backup/monthly" "$DATA_DIR/logs" "$DATA_DIR/state"
chmod 700 "$DATA_DIR" "$DATA_DIR/config" "$DATA_DIR/backup" "$DATA_DIR/backup/daily" \
    "$DATA_DIR/backup/weekly" "$DATA_DIR/backup/monthly" "$DATA_DIR/logs" "$DATA_DIR/state"
if [[ "$SYSTEM" -eq 1 ]]; then
    # Data dir must be owned by the service account; the install dir stays root-owned
    # (0755) so only root can replace the binary.
    chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
fi

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
CONF_LE=0
if [[ -f "$CONFIG_FILE" ]]; then
    # POSIX-safe extraction (grep -P is unavailable on Alpine/musl/macOS).
    CONF_CERT="$(sed -n 's/.*"cert_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$CONFIG_FILE" 2>/dev/null | head -1 || true)"
    # An operator-configured Let's Encrypt setup has no cert_path by design —
    # never "heal" (overwrite) such a config just because a cert.pfx exists.
    if grep -q '"lets_encrypt"' "$CONFIG_FILE" 2>/dev/null \
        && grep -q '"enabled"[[:space:]]*:[[:space:]]*true' "$CONFIG_FILE" 2>/dev/null; then
        CONF_LE=1
    fi
fi
# With --lets-encrypt we need no cert file at all (the server provisions its own via ACME
# and serves a bootstrap cert until the first order lands). Without it we need a cert when
# there is no config, or the existing config has none (an operator config without
# tls.cert_path cannot start on Linux — OptionsResolver rejects it), and no cert file is
# already in place.
NEED_CERT=0
if [[ -z "$LE_EMAIL" ]]; then
    if [[ ! -f "$CONFIG_FILE" ]]; then
        NEED_CERT=1
    elif [[ -z "$CONF_CERT" && ! -f "$CERT_FILE" ]]; then
        NEED_CERT=1
    fi
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
        # Write the pfx to a temp path first: a failed export (e.g. disk full)
        # must not leave a corrupt cert.pfx that later runs accept as-is.
        openssl pkcs12 -export -out "$TMP_CERT/cert.pfx" -inkey "$TMP_CERT/key.pem" \
            -in "$TMP_CERT/cert.pem" -passout pass: 2>/dev/null
        chmod 600 "$TMP_CERT/cert.pfx"
        mv -f "$TMP_CERT/cert.pfx" "$CERT_FILE"
        trap - EXIT
        rm -rf "$TMP_CERT"
        say "Wrote $CERT_FILE (self-signed, empty password — pin it on agents or install your CA)."
    fi
fi

# ── default config (never overwrite a functional operator config) ────────────
write_default_config() {
    say "Writing default $CONFIG_FILE"
    if [[ -n "$LE_EMAIL" ]]; then
        # Let's Encrypt mode: no cert_path — the server provisions + renews its own cert.
        # http_port must be reachable from the internet on the server's public IP (port 80
        # typically; a reverse proxy may forward /.well-known/acme-challenge/ instead).
        DOMAINS_JSON=""
        for d in "${LE_DOMAINS[@]}"; do DOMAINS_JSON+="\"$d\", "; done
        DOMAINS_JSON="${DOMAINS_JSON%, }"
        cat > "$CONFIG_FILE" <<EOF
{
  "urls": "https://0.0.0.0:$PORT",
  "tls": {
    "min_tls": "1.2",
    "preferred_tls": "1.3",
    "lets_encrypt": {
      "enabled": true,
      "domains": [$DOMAINS_JSON],
      "email": "$LE_EMAIL",
      "staging": false,
      "renew_days": 30,
      "http_port": ${HTTP_PORT:-80}
    }
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
        say "Let's Encrypt enabled for: ${LE_DOMAINS[*]} (challenge listener on port ${HTTP_PORT:-80})"
    else
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
    fi
    chmod 600 "$CONFIG_FILE"
}

if [[ ! -f "$CONFIG_FILE" ]]; then
    write_default_config
elif [[ -n "$LE_EMAIL" ]]; then
    say "Config exists — preserving $CONFIG_FILE (restart the service to apply any changes)."
elif [[ "$CONF_LE" -eq 1 ]]; then
    say "Config exists with Let's Encrypt enabled — preserving $CONFIG_FILE (no cert_path expected)."
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

# ── systemd unit ─────────────────────────────────────────────────────────────
mkdir -p "$(dirname "$UNIT_FILE")"
say "Writing $UNIT_FILE"
if [[ "$SYSTEM" -eq 1 ]]; then
    cat > "$UNIT_FILE" <<EOF
[Unit]
Description=Hyveman Server (ingest API, hardware poller, alerts, Blazor UI)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
# Runs as a dedicated unprivileged account; CAP_NET_BIND_SERVICE lets it bind
# ports 80/443 (https + the Let's Encrypt http-01 challenge listener) without root.
User=$SERVICE_USER
# Content root defaults to CWD; pin it to the install dir so wwwroot/static
# assets resolve deterministically regardless of where systemd launches us.
WorkingDirectory=$INSTALL_DIR
ExecStart="$INSTALL_DIR/hyveman-server" --data-dir "$DATA_DIR"
Environment="HYVEMAN_DATA_DIR=$DATA_DIR"
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=on-failure
RestartSec=5
UMask=0077

# Privileged ports only — nothing else.
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE

# Hardening: the server only needs its own data dir + network.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$DATA_DIR
RestrictSUIDSGID=true
ProtectKernelTunables=true
ProtectControlGroups=true
ProtectKernelModules=true
PrivateDevices=true

[Install]
WantedBy=$WANTED_BY
EOF
else
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
ExecStart="$INSTALL_DIR/hyveman-server" --data-dir "$DATA_DIR"
Environment="HYVEMAN_DATA_DIR=$DATA_DIR"
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
WantedBy=$WANTED_BY
EOF
fi

$SYSCTL daemon-reload

# ── enable + start ───────────────────────────────────────────────────────────
if [[ "$NO_START" -eq 0 ]]; then
    say "Enabling and starting $SERVICE_NAME..."
    $SYSCTL enable --now "$SERVICE_NAME" >/dev/null 2>&1 || true
    $SYSCTL restart "$SERVICE_NAME"
    if [[ "$SYSTEM" -eq 0 ]]; then
        # Linger keeps the service running after logout (best-effort; may need polkit).
        if ! loginctl enable-linger "${USER:-$(id -un)}" 2>/dev/null; then
            echo "    note: could not enable linger (may need: sudo loginctl enable-linger ${USER:-$(id -un)})."
            echo "    The service will stop when you log out unless linger is enabled."
        fi
    fi
else
    say "Enabling $SERVICE_NAME (not started)..."
    $SYSCTL enable "$SERVICE_NAME" >/dev/null 2>&1 || true
fi

# ── health check ─────────────────────────────────────────────────────────────
if [[ "$NO_START" -eq 0 ]] && command -v curl >/dev/null 2>&1; then
    EFFECTIVE_PORT="$PORT"
    if [[ -f "$CONFIG_FILE" ]]; then
        CONF_PORT="$(sed -n 's/.*"urls"[[:space:]]*:[[:space:]]*"https:\/\/[^":]*:\([0-9][0-9]*\)".*/\1/p' "$CONFIG_FILE" | head -1 || true)"
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
        echo "    Check: $SYSCTL status $SERVICE_NAME; $JRNL"
    fi
fi

# ── summary ──────────────────────────────────────────────────────────────────
MODE_LABEL="user service"
[[ "$SYSTEM" -eq 1 ]] && MODE_LABEL="system service (root-owned, dedicated '$SERVICE_USER' account)"
echo
echo "Hyveman server installed ($MODE_LABEL)."
echo "  Binary:    $INSTALL_DIR/hyveman-server"
echo "  Data dir:  $DATA_DIR"
echo "  Config:    $CONFIG_FILE"
echo "  Service:   $SYSCTL status $SERVICE_NAME"
echo "  Logs:      $JRNL"
if [[ "$NO_START" -eq 0 ]]; then
    echo "  UI:        https://localhost:${EFFECTIVE_PORT:-$PORT}/  (first-run passkey wizard)"
    echo "  Health:    curl -k https://localhost:${EFFECTIVE_PORT:-$PORT}/health -H 'X-Hyveman-Protocol: 1'"
fi
echo "  Next:      browse to the UI from this machine (localhost), register a passkey,"
echo "             then add hosts/channels in /admin."
if [[ -n "$LE_EMAIL" ]]; then
    echo
    echo "  Let's Encrypt: make sure port ${HTTP_PORT:-80} is reachable from the internet on"
    echo "             the server's public IP, and that the DNS A/AAAA records for"
    echo "             ${LE_DOMAINS[*]} point at this machine. The first certificate"
    echo "             arrives within a minute of first start (see $JRNL)."
fi
