#!/usr/bin/env bash
# Builds the Flatpak (task 122) from the committed tree into ~/.cache/hdmicaptain/flatpak and exports a bundle,
# packages/hdmi-captain.flatpak. Usage: flatpak.sh [--install]   (--install also installs it for this user).
# Needs flatpak + flatpak-builder (make linux-setup); it installs the GNOME runtime/SDK the manifest names and the
# matching .NET 10 SDK extension itself.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$HOME/.cache/hdmicaptain"
manifest="$repo/Apps/Linux/packaging/com.example.HDMICaptain.yml"
runtime="$(sed -n "s/^runtime-version: '\(.*\)'/\1/p" "$manifest")"
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install --user -y --noninteractive --or-update flathub "org.gnome.Platform//$runtime" "org.gnome.Sdk//$runtime" > /dev/null
# The .NET extension's branch is the freedesktop SDK the GNOME SDK is built on (51 → 26.08).
base="$(flatpak info --user -m "org.gnome.Sdk//$runtime" | sed -n '/^\[Extension org.freedesktop.Sdk.Extension\]/,/^\[/s/^version = //p' | head -1)"
flatpak install --user -y --noninteractive --or-update flathub "org.freedesktop.Sdk.Extension.dotnet10//$base" > /dev/null
# The package list must come from the SDK extension's own dotnet: its runtime packs are what the sandboxed publish asks for.
flatpak run --user --share=network --filesystem="$repo" --filesystem="$HOME/.cache" --command=sh   --env=PATH=/usr/bin:/usr/lib/sdk/dotnet10/bin --env=DOTNET_CLI_TELEMETRY_OPTOUT=1 --env=DOTNET_NOLOGO=1   "org.gnome.Sdk//$runtime" -c "python3 '$repo/tools/linux/flatpak-nuget-sources.py' '$repo/Apps/Linux/packaging/nuget-sources.json'"
if ! git -C "$repo" diff --quiet HEAD -- Apps/Linux/packaging/nuget-sources.json 2>/dev/null; then
  echo "note: nuget-sources.json changed; commit it so the git source and the list agree"
fi
mkdir -p "$out/packages"
flatpak-builder --user --force-clean --disable-rofiles-fuse --state-dir="$out/flatpak-state" \
  --repo="$out/flatpak-repo" "$out/flatpak-build" "$manifest"
flatpak build-bundle "$out/flatpak-repo" "$out/packages/hdmi-captain.flatpak" com.example.HDMICaptain
ls -l "$out/packages/hdmi-captain.flatpak"
if [ "${1:-}" = "--install" ]; then
  flatpak install --user -y --noninteractive --reinstall "$out/packages/hdmi-captain.flatpak"
fi
