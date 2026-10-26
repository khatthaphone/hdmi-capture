#!/usr/bin/env bash
# Builds the Flatpak (task 122) from the committed tree into ~/.cache/hdmicaptain/flatpak and exports a bundle,
# packages/hdmi-captain.flatpak. Usage: flatpak.sh [--install]   (--install also installs it for this user).
# Needs flatpak + flatpak-builder and the GNOME 48 SDK + dotnet10 extension (make linux-setup installs them).
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$HOME/.cache/hdmicaptain"
manifest="$repo/Apps/Linux/packaging/com.example.HDMICaptain.yml"
python3 "$repo/tools/linux/flatpak-nuget-sources.py" "$repo/Apps/Linux/packaging/nuget-sources.json"
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
