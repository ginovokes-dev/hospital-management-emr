#!/usr/bin/env bash
# The older component projects list their NuGet packages in packages.config and expect them in Code/Solutions/packages/<Id>.<Version>/
# (that is what "nuget restore" on Windows does). This puts them there using only the dotnet SDK.
#   usage: restore-packages-config.sh [Code folder]     (default ./Code)
set -euo pipefail
CODE="${1:-Code}"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
LIST="$WORK/packages.list"

grep -rho '<package id="[^"]*" version="[^"]*"' "$CODE"/Components/*/packages.config \
  | sed -E 's/<package id="([^"]*)" version="([^"]*)"/\1 \2/' | sort -u > "$LIST"
echo "$(wc -l < "$LIST") packages to place under $CODE/Solutions/packages"

# PackageDownload fetches exactly these versions without looking at compatibility or dependencies
{
  echo '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>'
  # one item per package; a package used at two versions lists both ("[1.0];[2.0]")
  awk '{ v[$1] = (v[$1] == "" ? "[" $2 "]" : v[$1] ";[" $2 "]") } END { for (i in v) print i, v[i] }' "$LIST" \
    | while read -r id vers; do echo "<PackageDownload Include=\"$id\" Version=\"$vers\" />"; done
  echo '</ItemGroup></Project>'
} > "$WORK/fetch.csproj"
dotnet restore "$WORK/fetch.csproj" --packages "$WORK/cache" -v:q

DEST="$CODE/Solutions/packages"; mkdir -p "$DEST"
missing=0
while read -r id ver; do
  lower="$(echo "$id" | tr 'A-Z' 'a-z')"
  src="$WORK/cache/$lower/$ver"
  [ -d "$src" ] || src="$WORK/cache/$lower/${ver%.0}"          # NuGet drops the last ".0" of a four-part version
  if [ -d "$src" ]; then
    mkdir -p "$DEST/$id.$ver"; cp -a "$src/." "$DEST/$id.$ver/"
  else
    echo "  missing: $id $ver"; missing=$((missing + 1))
  fi
done < "$LIST"
echo "done; missing: $missing"
[ "$missing" = 0 ]
