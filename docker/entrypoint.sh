#!/usr/bin/env bash
# Starts the application inside the container. Settings come from environment variables (see docker-compose.yml):
#   DB_HOST DB_PORT DB_PASSWORD EMR_DB ADMIN_DB      where the databases are
#   ADMIN_PASSWORD                                   password of the 'admin' login the first time the application starts (default 123)
#   FRESH_START=1                                    first start only: switch off the sample staff logins and hide the sample patients
#   FILES_DIR=/data/files                            folder for uploaded scans / documents (mount a volume there)
set -euo pipefail
: "${DB_HOST:=db}" "${DB_PORT:=1433}" "${EMR_DB:=DEV_DanpheEMR_INT}" "${ADMIN_DB:=DanpheAdmin}"
: "${DB_PASSWORD:?DB_PASSWORD is required}"

# the application keeps the database password in its own encrypted form
ENC="$(/app/docker/encrypt-for-danphe.sh "$DB_PASSWORD")"
# Enlist=false: Mono cannot enlist SQL connections in ambient transactions (the application's read-only ReadUncommitted pattern)
export Connectionstring="Data Source=${DB_HOST},${DB_PORT};Initial Catalog=${EMR_DB};User ID=sa;Password=${ENC};MultipleActiveResultSets=true;TrustServerCertificate=True;Enlist=false"
export ConnectionStringAdmin="Data Source=${DB_HOST},${DB_PORT};Initial Catalog=${ADMIN_DB};User ID=sa;Password=${ENC};TrustServerCertificate=True;Enlist=false"

export environment__isdevelopment=false            # no Swagger / directory listing
export ASPNETCORE_URLS="http://+:8080"
export LD_LIBRARY_PATH="/app/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export DOTNET_REFERENCE_ASSEMBLIES_PATH=/opt/refs/flat-461
export DANPHE_ADMIN_PASSWORD="${ADMIN_PASSWORD:-123}"
export DANPHE_FRESH_START="${FRESH_START:-0}"
export DANPHE_FILES_DIR="${FILES_DIR:-}"            # where uploaded scans / documents are kept (a volume)
export MONO_THREADS_PER_CPU=50

cd /app
exec mono bin/Debug/net461/DanpheEMR.exe
