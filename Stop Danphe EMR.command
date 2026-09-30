#!/bin/bash
# Double-click this file on a Mac to stop Danphe EMR. Your patients and data stay saved.
cd "$(dirname "$0")" || exit 1
./docker/stop.sh
read -r -p "Press Return to close this window. " _
