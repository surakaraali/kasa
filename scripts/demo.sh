#!/usr/bin/env bash
# Starts everything for the demo, in production mode, from a clean state.
#   scripts/demo.sh         start (logs in .demo/logs)
#   scripts/demo.sh reset   stop, empty the Kasa database, start again
#   scripts/demo.sh stop    stop the .NET services and the web app (Docker keeps running)
#   scripts/demo.sh web     rebuild and restart only the web app
# With KASA_PUBLIC_URL=https://kasa.surakaraali.dev the open banking flow uses that address (see scripts/tunnel.sh).
set -euo pipefail
cd "$(dirname "$0")/.."
LOGS=.demo/logs
mkdir -p "$LOGS"

# Builds and starts the web app. Two things keep a tab that was open during a rebuild working:
# - one id per build, shared by build and server (Next's version skew protection): such a tab reloads itself on
#   its next page change instead of running old code against the new server;
# - the previous builds' static files are put back next to the new ones, so until then the old code it asks for
#   still exists instead of answering 404 ("This page couldn't load").
start_web() {
  local keep=.demo/static-keep
  mkdir -p "$keep"
  [[ -d web/.next/static ]] && cp -R web/.next/static/. "$keep/"
  export NEXT_DEPLOYMENT_ID="kasa-$(date +%s)"
  local ci="${1:-}"
  (cd web && if [[ -n "$ci" ]]; then npm ci --silent; fi && npm run build \
    && { cp -Rn "../$keep/." .next/static/ || true; } && exec npm start) > "$LOGS/web.log" 2>&1 &
}

stop() {
  pkill -f "Kasa\.(Api|MockBanks|MockErp|ErpExporter)" 2>/dev/null || true
  # Next renames its process to "next-server", so the web app is stopped by its port.
  lsof -ti tcp:3000 -sTCP:LISTEN | xargs kill 2>/dev/null || true
}

case "${1:-start}" in
  stop) stop; echo "Durdu."; exit 0 ;;
  web)
    # Rebuild and restart only the web app; the services and the data stay as they are.
    lsof -ti tcp:3000 -sTCP:LISTEN | xargs kill 2>/dev/null || true
    start_web
    until curl -sf http://localhost:3000/giris > /dev/null; do sleep 1; done
    echo "Web yeniden başladı ($NEXT_DEPLOYMENT_ID)."; exit 0 ;;
  reset)
    stop
    set -a; source .env; set +a
    docker compose up -d --wait   # the database must be up before it can be emptied (after a reboot it is not)
    # The test banks and the test ERP keep their state in memory, so restarting them is their reset.
    docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b \
      -Q "IF DB_ID('Kasa') IS NOT NULL BEGIN ALTER DATABASE Kasa SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE Kasa; END"
    docker compose exec -T redis redis-cli --scan --pattern 'kasa:*' | xargs -r docker compose exec -T redis redis-cli del >/dev/null || true
    echo "Veritabanı ve Redis temizlendi." ;;
  start) ;;
  *) echo "kullanım: scripts/demo.sh [start|reset|stop|web]"; exit 1 ;;
esac

# Behind a public address (scripts/tunnel.sh), the open banking flow must use it instead of localhost.
if [[ -n "${KASA_PUBLIC_URL:-}" ]]; then
  export Web__BaseUrl="$KASA_PUBLIC_URL"
  export Banks__AcikBank__PublicUrl="$KASA_PUBLIC_URL/acikbank"
  export Banks__AcikBank__RedirectUri="$KASA_PUBLIC_URL/api/bank-connections/acikbank/callback"
  export MockBanks__RedirectUris__2="$KASA_PUBLIC_URL/api/bank-connections/acikbank/callback"
  echo "Genel adres: $KASA_PUBLIC_URL"
fi

docker compose up -d --wait
# No build servers: they would outlive the script and keep its output open.
dotnet build -c Release --nologo -v q --disable-build-servers > "$LOGS/build.log" 2>&1
for service in MockBanks MockErp ErpExporter Api; do
  # The http launch profile: Development settings (local addresses) and telemetry to the Aspire dashboard.
  nohup dotnet run --project "src/Kasa.$service" -c Release --no-build --launch-profile http > "$LOGS/$service.log" 2>&1 &
done
# In the background with its own log, so nothing keeps this script's output open.
start_web ci

for url in http://localhost:5102/health http://localhost:5103/health http://localhost:5101/alive http://localhost:5100/alive; do
  until curl -sf "$url" > /dev/null; do sleep 1; done
done
until curl -sf http://localhost:3000/giris > /dev/null; do sleep 1; done
cat <<TEXT
Hazır.
  Kasa            http://localhost:3000
  Test ERP fişleri http://localhost:5103
  İzleme paneli   http://localhost:18888
  RabbitMQ        http://localhost:15672
Loglar: $LOGS
TEXT
