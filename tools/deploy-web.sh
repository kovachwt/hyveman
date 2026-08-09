#!/usr/bin/env bash
# Redeploy the built frontend (hyveman-web/dist) to ~/www/hyveman/current.
# Usage: ./deploy-web.sh   (run from repo root; must run `npm run build` first)
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WEB_ROOT="$HOME/www/hyveman"

if [[ ! -d "$REPO/hyveman-web/dist" ]]; then
  echo "dist/ not found — run: (cd hyveman-web && npm run build)" >&2
  exit 1
fi

mkdir -p "$WEB_ROOT/releases"
STAMP="$(date +%Y%m%d-%H%M%S)"
cp -r "$REPO/hyveman-web/dist" "$WEB_ROOT/releases/$STAMP"
ln -sfn "$WEB_ROOT/releases/$STAMP" "$WEB_ROOT/current"

# nginx (www-data) must be able to traverse /home/<user> and read the files.
chmod o+x "$HOME" "$WEB_ROOT"
find "$WEB_ROOT/current" -type d -exec chmod 755 {} +
find "$WEB_ROOT/current" -type f -exec chmod 644 {} +

echo "deployed: $WEB_ROOT/current -> $WEB_ROOT/releases/$STAMP"
