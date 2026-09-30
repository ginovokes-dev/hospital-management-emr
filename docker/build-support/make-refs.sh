#!/usr/bin/env bash
# A private copy (symlinks) of Mono's .NET Framework 4.6.1 reference-assembly folder, plus an alias for every
# <Reference Include="Name"> in the project files whose spelling differs in letter-case from the real DLL name
# (Windows does not care, Linux does). No project file is edited.
#   usage: make-refs.sh [repo folder]      (default: current folder)   env: MONO_REF_DIR, PRIVATE_REF_DIR
set -euo pipefail
TREE="${1:-.}"
SRC="${MONO_REF_DIR:-/usr/lib/mono/4.6.1-api}"
DST="${PRIVATE_REF_DIR:-/refs/4.6.1-api}"
mkdir -p "$DST"
for f in "$SRC"/*; do ln -sf "$f" "$DST/$(basename "$f")"; done
made=0
while read -r name; do
  [ -z "$name" ] && continue
  [ -e "$DST/$name.dll" ] && continue
  real="$(ls "$SRC" | grep -i -x -F "$name.dll" | head -1 || true)"
  if [ -n "$real" ]; then ln -s "$SRC/$real" "$DST/$name.dll"; made=$((made + 1)); echo "  alias $name.dll -> $real"; fi
done < <(grep -rhoE '<Reference Include="[^",]+' "$TREE"/Code --include=*.csproj 2>/dev/null | sed 's/.*Include="//' | sort -u)
echo "private reference folder $DST ready ($made case aliases)"
