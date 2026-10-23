#!/bin/sh
# Launcher installed as /usr/bin/hdmi-captain by the .deb/.rpm (task 122).
# Under WSL, WSLg's Mesa needs the d3d12 driver picked by hand (otherwise llvmpipe); elsewhere nothing changes.
if [ -n "${WSL_DISTRO_NAME:-}" ]; then
  export GALLIUM_DRIVER="${GALLIUM_DRIVER:-d3d12}"
fi
exec /opt/hdmi-captain/hdmi-captain "$@"
