#!/usr/bin/env bash
# Fetch Steamworks.NET native + managed binaries from the upstream Standalone release.
# Run once after cloning, before `dotnet build`.
#
# This deliberately doesn't commit Valve-redistributable binaries to the repo.

set -euo pipefail

cd "$(dirname "$0")"

if [[ -f lib/Steamworks.NET.dll && -f native/libsteam_api.so ]]; then
    echo "deps already present (lib/Steamworks.NET.dll + native/libsteam_api.so)."
    echo "delete them and re-run to update."
    exit 0
fi

echo "looking up latest Steamworks.NET release..."
release_json=$(curl -sSL https://api.github.com/repos/rlabrecque/Steamworks.NET/releases/latest)
asset_url=$(echo "$release_json" | grep '"browser_download_url"' | grep 'Standalone' | head -1 | sed -E 's/.*"([^"]+)".*/\1/')

if [[ -z "$asset_url" ]]; then
    echo "ERROR: could not find Standalone asset URL in latest release" >&2
    exit 1
fi

echo "downloading: $asset_url"
tmpdir=$(mktemp -d)
trap 'rm -rf "$tmpdir"' EXIT
curl -sSL -o "$tmpdir/swnet.zip" "$asset_url"

mkdir -p lib native
unzip -j -o "$tmpdir/swnet.zip" "OSX-Linux-x64/Steamworks.NET.dll" -d lib/
unzip -j -o "$tmpdir/swnet.zip" "OSX-Linux-x64/libsteam_api.so" -d native/

echo
echo "✓ deps fetched:"
ls -la lib/Steamworks.NET.dll native/libsteam_api.so
echo
echo "next: dotnet build"
