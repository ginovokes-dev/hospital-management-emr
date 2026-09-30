#!/usr/bin/env bash
# Stops Danphe EMR. Patients and everything else stay saved and are back the next time you start it.
# To erase everything and start from nothing:  docker compose down -v
cd "$(dirname "$0")/.."
docker compose stop
echo "Danphe EMR is stopped. Your data is kept."
