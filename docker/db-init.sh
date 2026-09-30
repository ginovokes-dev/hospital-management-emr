#!/usr/bin/env bash
# Creates the two Danphe databases from the sample data that ships in this repository - once. When they exist already (every start
# after the first) nothing happens. Runs in the SQL Server image, next to the database server.
#   needs: /db (the repository's Database folder, read-only), the SQL Server data volume at /var/opt/mssql
set -uo pipefail
: "${DB_HOST:=db}" "${EMR_DB:=DEV_DanpheEMR_INT}" "${ADMIN_DB:=DanpheAdmin}"
: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is required}"
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
sql() { "$SQLCMD" -C -S "$DB_HOST" -U sa -P "$MSSQL_SA_PASSWORD" "$@"; }
scalar() { sql -h -1 -W -Q "SET NOCOUNT ON; $1" 2>/dev/null | tr -d '[:space:]'; }
exists() { [ "$(scalar "SELECT COUNT(*) FROM sys.databases WHERE name = N'$1'")" != "0" ]; }
online() { [ "$(scalar "SELECT state_desc FROM sys.databases WHERE name = N'$1'")" = "ONLINE" ]; }

echo "waiting for SQL Server ..."
for i in $(seq 1 90); do [ "$(scalar 'SELECT 1')" = "1" ] && break; sleep 2; done
[ "$(scalar 'SELECT 1')" = "1" ] || { echo "SQL Server did not come up"; exit 1; }

if exists "$EMR_DB"; then
  echo "hospital database $EMR_DB is already there"
else
  echo "creating the hospital database from the sample data (first start only, about a minute) ..."
  mkdir -p /var/opt/mssql/backup
  python3 -m zipfile -e "/db/2. EMR-Db/DanpheInternationalDB/Dev_DanpheEMR_INT1.zip" /var/opt/mssql/backup || { echo "could not unpack the sample database"; exit 1; }
  BAK="$(ls /var/opt/mssql/backup/*.bak | head -1)"
  # SQL Server for Linux has no FILESTREAM, so only the main file group is restored (the three attachment tables are recreated by the application)
  sql -Q "RESTORE DATABASE [$EMR_DB] FILEGROUP = N'PRIMARY' FROM DISK = N'$BAK' WITH PARTIAL, REPLACE, RECOVERY,
            MOVE N'Danphe_MNK_FINAL' TO N'/var/opt/mssql/data/$EMR_DB.mdf', MOVE N'Danphe_MNK_FINAL_log' TO N'/var/opt/mssql/data/${EMR_DB}_log.ldf'" 2>&1 | grep -E "successfully processed|Msg [0-9]+" | grep -v "Msg 3127" | head -5
  rm -f "$BAK"
  online "$EMR_DB" || { echo "the hospital database could not be restored"; exit 1; }
fi

if exists "$ADMIN_DB"; then
  echo "administration database $ADMIN_DB is already there"
else
  echo "creating the administration database ..."
  iconv -f UTF-16 -t UTF-8 "/db/1. Admin-Db/1. DanpheAdmin_CompleteDB.sql" | sed 's/\r$//' | sql -i /dev/stdin 2>&1 | grep -E "^Msg " | head -5 || true
  online "$ADMIN_DB" || { echo "the administration database could not be created"; exit 1; }
fi
echo "databases ready"
