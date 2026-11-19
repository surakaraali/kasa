#!/usr/bin/env bash
# Puts the running demo (scripts/demo.sh) on https://kasa.surakaraali.dev through a Cloudflare Tunnel.
# Only the web app (port 3000) is exposed; the API, the test services' controls, Swagger and the dashboards are not.
# Run it only while you demo: anyone with the address can register a company.
#   KASA_PUBLIC_URL=https://kasa.surakaraali.dev scripts/demo.sh reset
#   scripts/tunnel.sh
set -euo pipefail
exec cloudflared tunnel --no-autoupdate run --url http://localhost:3000 kasa
