#!/bin/bash
# Double-click this file on a Mac to start Danphe EMR (it opens your web browser when it is ready).
cd "$(dirname "$0")" || exit 1
./docker/start.sh
echo
echo "You can close this window. The system keeps running in the background; use \"Stop Danphe EMR\" to stop it."
read -r -p "Press Return to close this window. " _
