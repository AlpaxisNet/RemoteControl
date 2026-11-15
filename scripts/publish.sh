#!/usr/bin/env bash
# Builds the packages users download: one folder (and one archive) per system, with everything the program needs inside.
#
#   scripts/publish.sh [linux-x64] [win-x64] [osx-arm64] ...      (default: linux-x64 and win-x64)
#
# Each folder holds the program (RemoteControl / RemoteControl.exe, with .NET inside: nothing has to be installed), the few native
# libraries of the user interface beside it, and the pictures, sounds, fonts, language files and default options
# (images/, sounds/, fonts/, language-specs/, control2config.txt). Output: publish/<system>/ and publish/RemoteControl-<version>-<system>.{tar.gz,zip}.
set -euo pipefail
cd "$(dirname "$0")/.."
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
[ $# -gt 0 ] || set -- linux-x64 win-x64
for system in "$@"; do
    out=publish/$system
    rm -rf "$out"
    dotnet publish src/RemoteControl.App/RemoteControl.App.csproj -c Release -r "$system" --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=embedded \
        -p:EnableCompressionInSingleFile=true -o "$out" --nologo -v:quiet
    rm -f "$out"/*.pdb "$out"/*.xml
    name=RemoteControl-$version-$system
    case "$system" in
        win-*) (cd publish && rm -f "$name.zip" && if command -v zip >/dev/null; then cp -r "$system" "$name" && zip -qr "$name.zip" "$name" && rm -rf "$name"; fi) ;;
        *) tar -C publish --transform "s|^$system|$name|" -czf "publish/$name.tar.gz" "$system" ;;
    esac
    echo "$system: $(du -sh "$out" | cut -f1) in $out"
done
