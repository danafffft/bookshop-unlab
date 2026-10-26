#!/usr/bin/env bash
set -euo pipefail
: "${DATABASE_URL:?Set the Neon PostgreSQL connection URL in Render secrets}"
: "${ADMIN_EMAIL:?Set ADMIN_EMAIL in Render secrets}"
: "${ADMIN_PASSWORD:?Set ADMIN_PASSWORD in Render secrets}"
export PORT="${PORT:-10000}"
export ADDR="0.0.0.0:${PORT}"
export PUBLIC_ORIGIN="${PUBLIC_ORIGIN:-${RENDER_EXTERNAL_URL:-}}"
: "${PUBLIC_ORIGIN:?Set PUBLIC_ORIGIN to the public HTTPS origin}"
if [[ "$PUBLIC_ORIGIN" != https://* ]]; then
  echo 'Public deployment requires an HTTPS PUBLIC_ORIGIN' >&2
  exit 1
fi
export COOKIE_SECURE=true
api_pid=''
gateway_pid=''
cleanup() {
  [[ -z "$api_pid" ]] || kill -TERM "$api_pid" 2>/dev/null || true
  [[ -z "$gateway_pid" ]] || kill -TERM "$gateway_pid" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 0' TERM INT
# ASP.NET binds only to this container's loopback. Go is the public entry point.
dotnet /app/api/CherryBooks.dll &
api_pid=$!
/app/gateway &
gateway_pid=$!
# A failure in either process shuts down the container, so the host can restart it.
set +e
wait -n "$api_pid" "$gateway_pid"
status=$?
set -e
if [[ "$status" -eq 0 ]]; then status=1; fi
exit "$status"
