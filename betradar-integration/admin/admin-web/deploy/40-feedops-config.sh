#!/bin/sh
# Writes the runtime config the Angular app reads at startup (see core/config.ts).
set -eu
rm -f /usr/share/nginx/html/config.json
cat > /usr/share/nginx/html/config.json <<JSON
{
  "apiBaseUrl": "${FEEDOPS_API_BASE_URL:-}",
  "auth": {
    "enabled": ${FEEDOPS_AUTH_ENABLED:-true},
    "authority": "${FEEDOPS_AUTH_AUTHORITY:-}",
    "clientId": "${FEEDOPS_AUTH_CLIENT_ID:-feed-ops-web}",
    "scope": "${FEEDOPS_AUTH_SCOPE:-openid profile}"
  },
  "grafanaUrl": "${FEEDOPS_GRAFANA_URL:-}"
}
JSON
# nginx workers run as an unprivileged user and must be able to read it.
chmod 644 /usr/share/nginx/html/config.json
