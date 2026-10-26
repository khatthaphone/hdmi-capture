#!/bin/sh
# Launcher inside the Flatpak (task 122): the self-contained app lives in /app/lib/hdmi-captain.
exec /app/lib/hdmi-captain/hdmi-captain "$@"
