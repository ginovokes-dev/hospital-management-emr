#!/usr/bin/env bash
# Makes the (Windows-built) application folder run under Mono: the NuGet build of System.Runtime.InteropServices.RuntimeInformation
# always reports "Windows", which sends Kestrel down its Windows code paths. Mono ships a proper type-forwarding version of the
# same assembly; use that and point the binding redirect at it.
#   usage: fix-mono.sh <folder with DanpheEMR.exe>
set -euo pipefail
OUT=${1:?folder with DanpheEMR.exe}
FACADE=/usr/lib/mono/4.5/Facades/System.Runtime.InteropServices.RuntimeInformation.dll
cp -f "$FACADE" "$OUT/System.Runtime.InteropServices.RuntimeInformation.dll"
VER=$(csharp -e "System.Reflection.AssemblyName.GetAssemblyName(\"$FACADE\").Version.ToString()" 2>/dev/null | tr -d '\r\n"' || true)
case "$VER" in [0-9]*.[0-9]*.[0-9]*.[0-9]*) ;; *) VER=4.0.3.0;; esac
perl -0777 -i -pe 's{(<assemblyIdentity name="System\.Runtime\.InteropServices\.RuntimeInformation"[^>]*/>\s*<bindingRedirect )oldVersion="[^"]*" newVersion="[^"]*"}{$1oldVersion="0.0.0.0-'"$VER"'" newVersion="'"$VER"'"}' "$OUT/DanpheEMR.exe.config"
grep -q "newVersion=\"$VER\"" "$OUT/DanpheEMR.exe.config" && echo "RuntimeInformation facade $VER installed"
