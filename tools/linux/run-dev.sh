#!/usr/bin/env bash
# Launches a built Linux app with the dev-environment fixes (task 105). Usage: run-dev.sh <app.dll|exe> [args…]
# Under WSL: Mesa's d3d12 driver on the NVIDIA adapter (WSLg otherwise falls back to llvmpipe, or picks the iGPU),
# and a private D-Bus session because WSL has no user bus (GApplication and portals want one).
set -euo pipefail
app="$1"; shift
if [ -n "${WSL_DISTRO_NAME:-}" ]; then
  export GALLIUM_DRIVER="${GALLIUM_DRIVER:-d3d12}"
  export MESA_D3D12_DEFAULT_ADAPTER_NAME="${MESA_D3D12_DEFAULT_ADAPTER_NAME:-NVIDIA}"
  export GSK_RENDERER="${GSK_RENDERER:-ngl}"
fi
run=(); case "$app" in *.dll) run=(dotnet "$app");; *) run=("$app");; esac
if [ ! -S "${DBUS_SESSION_BUS_ADDRESS#unix:path=}" ]; then
  exec dbus-run-session -- "${run[@]}" "$@"
fi
exec "${run[@]}" "$@"
