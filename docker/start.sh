#!/usr/bin/env bash
# Starts Danphe EMR (database + web application) with Docker and opens it in the browser.
# Works on macOS, Linux and GitHub Codespaces.   usage: docker/start.sh [--no-open]
set -uo pipefail
cd "$(dirname "$0")/.."
OPEN=1; [ "${1:-}" = "--no-open" ] && OPEN=0

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
warn() { printf '\n\033[1;31m%s\033[0m\n' "$*"; }

if ! command -v docker >/dev/null 2>&1; then
  warn "Docker is not installed on this computer."
  echo "Install \"Docker Desktop\" (free) from  https://www.docker.com/products/docker-desktop/  , start it once,"
  echo "then run this again. On a very old Mac that Docker Desktop does not support, use the browser-only"
  echo "option in START-HERE.md (GitHub Codespaces)."
  [ "$(uname)" = "Darwin" ] && [ "$OPEN" = 1 ] && open "https://www.docker.com/products/docker-desktop/"
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  if [ "$(uname)" = "Darwin" ]; then
    say "Starting Docker Desktop ..."
    open -a Docker >/dev/null 2>&1 || true
    for i in $(seq 1 90); do docker info >/dev/null 2>&1 && break; sleep 2; done
  else
    # a Linux server / a Codespace that has only just started: give the Docker service a moment
    for i in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 2; done
  fi
  docker info >/dev/null 2>&1 || { warn "Docker is not running. Start Docker Desktop, wait until it says \"running\", then run this again."; exit 1; }
fi
if ! docker compose version >/dev/null 2>&1; then
  warn "This Docker is too old (it has no \"docker compose\"). Please update Docker Desktop."
  exit 1
fi

# a private database password, created once
if [ ! -f .env ]; then
  if docker volume inspect danphe-emr_dbdata >/dev/null 2>&1; then
    warn "The database already exists but the file .env (with its password) is missing."
    echo "Put the old .env back, or start from scratch with:  docker compose down -v   (this DELETES all patients)"
    exit 1
  fi
  PW="Dp$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 14)#1"
  cp .env.example .env
  sed -i.bak "s/^DB_PASSWORD=.*/DB_PASSWORD=$PW/" .env && rm -f .env.bak
  echo "created .env with a private database password"
fi

say "Starting Danphe EMR. The first start builds the program and creates the database: 10-25 minutes. Later starts take a minute."
docker compose up -d --build || { warn "Docker could not start the system. See the messages above."; exit 1; }

PORT="$(grep -E '^APP_PORT=' .env 2>/dev/null | cut -d= -f2)"; PORT="${PORT:-8080}"
URL="http://localhost:$PORT"
say "Waiting for the application to be ready ..."
for i in $(seq 1 180); do
  code="$(curl -s -o /dev/null -m 5 -w '%{http_code}' "$URL/Account/Login" 2>/dev/null || true)"
  [ "$code" = "200" ] && break
  sleep 5
done
if [ "${code:-}" != "200" ]; then
  warn "The application did not answer yet. Look at the log with:  docker compose logs app"
  exit 1
fi

say "Danphe EMR is ready:  $URL"
# in GitHub Codespaces the system is reached through a private forwarding address, not through localhost
if [ -n "${CODESPACE_NAME:-}" ] && [ -n "${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-}" ]; then
  echo "   In GitHub Codespaces, open:  https://${CODESPACE_NAME}-${PORT}.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN}   (or: PORTS tab > globe icon next to ${PORT})"
fi
echo "   Administrator sign-in:   username  admin      password  123      (change it after the first sign-in)"
echo "   The administrator adds doctors and gives them their own logins under \"Manage Doctors\"."
if [ "$OPEN" = 1 ]; then
  if command -v open >/dev/null 2>&1; then open "$URL"; elif command -v xdg-open >/dev/null 2>&1; then xdg-open "$URL" >/dev/null 2>&1 || true; fi
fi
