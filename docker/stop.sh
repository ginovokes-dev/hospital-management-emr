#!/usr/bin/env bash
# Stops Danphe EMR. Patients, logins and uploaded files are kept and are back the next time it starts.
cd "$(dirname "$0")/.."
docker compose stop app
# write everything the database still holds in memory to disk first, so the next start needs no recovery
PW="$(grep -E '^DB_PASSWORD=' .env 2>/dev/null | cut -d= -f2-)"
PW="${PW:-Danphe#Local2025}"
for db in DEV_DanpheEMR_INT DanpheAdmin; do
  docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$PW" -d "$db" -Q "CHECKPOINT" >/dev/null 2>&1 || true
done
docker compose stop
echo "Danphe EMR is stopped. Your data is kept."
