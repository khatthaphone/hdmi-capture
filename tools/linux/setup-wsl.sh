#!/usr/bin/env bash
# One-time (idempotent) setup of a Linux dev box for Apps/Linux (task 105).
# Run as root: `sudo bash tools/linux/setup-wsl.sh [user]`, or from Windows `wsl -d Ubuntu-24.04 -u root -- bash tools/linux/setup-wsl.sh <user>`.
# Installs the build/runtime packages and the .NET 10 SDK, and the capture-card udev rule.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
user="${1:-${SUDO_USER:-}}"

export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q --no-install-recommends \
  build-essential cmake ninja-build pkg-config rsync curl ca-certificates \
  libgtk-4-dev libadwaita-1-dev libegl-dev libgles-dev \
  libturbojpeg0-dev libpulse-dev libasound2-dev libudev-dev \
  v4l-utils mesa-utils pulseaudio-utils alsa-utils usbutils \
  libavcodec-dev libavformat-dev libavutil-dev libswresample-dev \
  ffmpeg gtk-4-examples

# .NET 10 SDK, same feature band as Apps/Windows/global.json.
if ! /usr/share/dotnet/dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
fi

install -m 0644 "$here/99-hdmicaptain.rules" /etc/udev/rules.d/99-hdmicaptain.rules
udevadm control --reload 2>/dev/null || true

if [ -n "$user" ]; then usermod -aG video,audio,plugdev "$user"; fi
dotnet --version
echo "setup-wsl: done"
