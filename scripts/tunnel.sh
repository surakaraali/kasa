#!/usr/bin/env bash
# Puts the running demo (scripts/demo.sh) on https://kasa.surakaraali.dev through a Cloudflare Tunnel.
# Only the web app (port 3000) is exposed; the API, the test services' controls, Swagger and the dashboards are not.
# Run it only while you demo: anyone with the address can register a company.
#   KASA_PUBLIC_URL=https://kasa.surakaraali.dev scripts/demo.sh reset
#   scripts/tunnel.sh
set -euo pipefail
# Only a demo started in public mode goes on the internet: there the sign-in key is secret and opening a company needs
# the sign-up code. Started without it, the repo's development key would be live and sign-up open to anyone.
curl -sf http://localhost:3000/api/auth/register | grep -q '"codeRequired":true' \
  || { echo "Demo genel modda değil. Önce: KASA_PUBLIC_URL=https://kasa.surakaraali.dev scripts/demo.sh start"; exit 1; }
exec cloudflared tunnel --no-autoupdate run --url http://localhost:3000 kasa
