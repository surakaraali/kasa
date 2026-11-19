#!/usr/bin/env bash
# Fills a fresh demo (after scripts/demo.sh reset) with one company that has everything connected:
# ERP firm 001 with its current accounts, both test banks, and an accountant and a viewer in the team.
# The accounts are test accounts of this demo only (fake data, local database).
set -euo pipefail
BASE="${KASA_PUBLIC_URL:-http://localhost:3000}"   # through the web app, like a browser: the bank's state cookie must match
# The demo accounts' password stays out of the public repo: made once, kept in .demo/demo-sifresi.
SECRET="$(dirname "$0")/../.demo/demo-sifresi"
mkdir -p "$(dirname "$SECRET")"
[[ -s "$SECRET" ]] || printf 'Kasa-%s-9x' "$(openssl rand -hex 6)" > "$SECRET"
PASSWORD=$(cat "$SECRET")
CODE=$(cat "$(dirname "$0")/../.demo/kayit-kodu" 2>/dev/null || true)   # set by scripts/demo.sh behind the public address
JAR=$(mktemp); trap 'rm -f "$JAR"' EXIT
json() { python3 -c "import sys,json; print(json.load(sys.stdin)$1)"; }

TOKEN=$(curl -sf -c "$JAR" -b "$JAR" "$BASE/api/auth/register" -H 'content-type: application/json' \
  -d "{\"companyName\":\"Anadolu Tekstil A.Ş.\",\"fullName\":\"Ayşe Demir\",\"email\":\"demo@kasa.test\",\"password\":\"$PASSWORD\",\"signUpCode\":\"$CODE\"}" | json '["accessToken"]') \
  || { echo "demo@kasa.test zaten var: önce scripts/demo.sh reset"; exit 1; }
AUTH="authorization: Bearer $TOKEN"

curl -sf -X PUT "$BASE/api/company/erp" -H "$AUTH" -H 'content-type: application/json' -d '{"firmCode":"001"}'
curl -sf -X POST "$BASE/api/current-accounts/import" -H "$AUTH" > /dev/null
curl -sf -X POST "$BASE/api/bank-connections/klasikbank" -H "$AUTH" -H 'content-type: application/json' \
  -d '{"username":"kasa","password":"klasik-dev-pass"}' > /dev/null

# Open banking as a browser does it: start, the bank's approval page, approve, back to Kasa.
URL=$(curl -sf -c "$JAR" -b "$JAR" -X POST "$BASE/api/bank-connections/acikbank" -H "$AUTH" | json '["authorizationUrl"]')
CONSENT=$(python3 -c 'import sys,urllib.parse as u; print(u.parse_qs(u.urlparse(sys.argv[1]).query)["consent_id"][0])' "$URL")
curl -sf -o /dev/null "$URL"
BACK=$(curl -s -o /dev/null -w '%{redirect_url}' -X POST "${URL%%\?*}" -d "consent_id=$CONSENT&decision=approve")
curl -s -o /dev/null -b "$JAR" "$BACK"

for who in "muhasebe@kasa.test:Accountant:Mehmet Kaya" "izleyici@kasa.test:Viewer:Zeynep Arslan"; do
  IFS=: read -r email role name <<< "$who"
  INVITE=$(curl -sf -X POST "$BASE/api/invitations" -H "$AUTH" -H 'content-type: application/json' -d "{\"email\":\"$email\",\"role\":\"$role\"}" | json '["token"]')
  curl -sf -o /dev/null "$BASE/api/auth/accept-invitation" -H 'content-type: application/json' \
    -d "{\"token\":\"$INVITE\",\"fullName\":\"$name\",\"password\":\"$PASSWORD\"}"
done

sleep 3
MOVES=$(curl -sf "$BASE/api/transactions?limit=200" -H "$AUTH" | json '["items"].__len__()')
echo "Demo şirketi hazır: Anadolu Tekstil A.Ş., $MOVES hareket, iki banka, ERP 001."
echo "Girişler: demo@kasa.test (yönetici), muhasebe@kasa.test (muhasebeci), izleyici@kasa.test (izleyici); şifre .demo/demo-sifresi dosyasında"
