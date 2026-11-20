#!/usr/bin/env bash
# The demo database (SQL Server in Docker), with nothing to install:
#   scripts/db.sh                              interactive: write a query, then GO on its own line; :quit to leave
#   scripts/db.sh "SELECT TOP 5 * FROM BankTransactions"
#   scripts/db.sh -f docs/demo-sorgulari.sql   every query in a file
# KASA_DB=Kasa_Test scripts/db.sh ...          the database the tests use
set -euo pipefail
cd "$(dirname "$0")/.."
PASSWORD=$(grep '^MSSQL_SA_PASSWORD=' .env | cut -d= -f2-)
SQLCMD=(/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d "${KASA_DB:-Kasa}" -s "|")
if [[ $# -eq 0 ]]; then
  exec docker exec -it -e SQLCMDPASSWORD="$PASSWORD" kasa-sqlserver-1 "${SQLCMD[@]}" -W
elif [[ $1 == -f ]]; then
  docker exec -i -e SQLCMDPASSWORD="$PASSWORD" kasa-sqlserver-1 "${SQLCMD[@]}" -i /dev/stdin < "$2"
else
  docker exec -e SQLCMDPASSWORD="$PASSWORD" kasa-sqlserver-1 "${SQLCMD[@]}" -W -Q "SET NOCOUNT ON; $1"
fi
