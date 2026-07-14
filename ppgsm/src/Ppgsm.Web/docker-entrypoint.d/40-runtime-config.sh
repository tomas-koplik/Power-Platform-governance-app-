#!/bin/sh
set -eu

escape_js() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

cat > /usr/share/nginx/html/config.js <<EOF
window.__PPGSM_CONFIG__ = {
  entraClientId: "$(escape_js "${PPGSM_ENTRA_CLIENT_ID:?PPGSM_ENTRA_CLIENT_ID is required}")",
  entraAuthority: "$(escape_js "${PPGSM_ENTRA_AUTHORITY:?PPGSM_ENTRA_AUTHORITY is required}")",
  apiScope: "$(escape_js "${PPGSM_API_SCOPE:?PPGSM_API_SCOPE is required}")",
  apiBaseUrl: "$(escape_js "${PPGSM_API_BASE_URL:?PPGSM_API_BASE_URL is required}")",
  dataAdapter: "live"
};
EOF