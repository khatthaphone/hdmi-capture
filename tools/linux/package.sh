#!/usr/bin/env bash
# Builds the Linux packages (task 122) from Apps/Linux into $OUT (default ~/.cache/hdmicaptain/packages):
#   hdmi-captain-<ver>-linux-x64.tar.gz   self-contained publish (CI, fallback)
#   hdmi-captain_<ver>_amd64.deb          /opt/hdmi-captain + launcher, desktop entry, icons, metainfo, udev rule
#   hdmi-captain-<ver>.x86_64.rpm         the same, when rpmbuild is installed
# The Flatpak is built separately from Apps/Linux/packaging/com.example.HDMICaptain.yml with flatpak-builder.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
version="${VERSION:-0.1.0}"
out="${OUT:-$HOME/.cache/hdmicaptain/packages}"
work="$out/work"
rm -rf "$work"
mkdir -p "$work" "$out"

echo "publish (Release, self-contained linux-x64)"
dotnet publish "$repo/Apps/Linux/src/HDMICaptain.Linux.App/HDMICaptain.Linux.App.csproj" -c Release -r linux-x64 --self-contained \
  -p:PublishReadyToRun=true -p:Version="$version" --artifacts-path "$HOME/.cache/hdmicaptain/release" -o "$work/app" -nologo -v:q

stage() {  # $1 = root
  local root="$1"
  mkdir -p "$root/opt/hdmi-captain" "$root/usr/bin" "$root/usr/share/applications" "$root/usr/share/metainfo" "$root/usr/lib/udev/rules.d"
  cp -a "$work/app/." "$root/opt/hdmi-captain/"
  install -m 0755 "$repo/Apps/Linux/packaging/hdmi-captain.sh" "$root/usr/bin/hdmi-captain"
  install -m 0644 "$repo/Apps/Linux/packaging/com.example.HDMICaptain.desktop" "$root/usr/share/applications/"
  install -m 0644 "$repo/Apps/Linux/packaging/com.example.HDMICaptain.metainfo.xml" "$root/usr/share/metainfo/"
  install -m 0644 "$repo/tools/linux/99-hdmicaptain.rules" "$root/usr/lib/udev/rules.d/60-hdmicaptain.rules"
  (cd "$repo/Apps/Linux/packaging/icons" && find . -name '*.png' -exec install -D -m 0644 {} "$root/usr/share/icons/{}" \;)
}

rm -f "$work/app/libhcrender_testctx.so" "$work/app/"*.pdb  # test helper and symbols stay out of packages

echo "tar.gz"
tarroot="$work/tar/hdmi-captain-$version"
mkdir -p "$tarroot"
cp -a "$work/app/." "$tarroot/"
cp "$repo/Apps/Linux/packaging/com.example.HDMICaptain.desktop" "$repo/tools/linux/99-hdmicaptain.rules" "$tarroot/"
tar -C "$work/tar" -czf "$out/hdmi-captain-$version-linux-x64.tar.gz" "hdmi-captain-$version"

echo "deb"
debroot="$work/deb"
stage "$debroot"
mkdir -p "$debroot/DEBIAN"
size=$(du -sk "$debroot/opt" | cut -f1)
cat > "$debroot/DEBIAN/control" <<CONTROL
Package: hdmi-captain
Version: $version
Section: video
Priority: optional
Architecture: amd64
Installed-Size: $size
Depends: libgtk-4-1 (>= 4.12), libadwaita-1-0 (>= 1.4), libturbojpeg, libasound2 | libasound2t64, libpulse0, libgles2, libegl1
Recommends: pipewire-pulse | pulseaudio
Maintainer: HDMI Captain <support@example.com>
Homepage: https://example.com/hdmi-captain
Description: Low-latency viewer for USB HDMI capture cards
 Shows a UVC HDMI capture card with its sound, recovers by itself when the card
 or source drops out, and keeps capture running in the background.
CONTROL
cat > "$debroot/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if command -v udevadm >/dev/null 2>&1; then udevadm control --reload 2>/dev/null || true; udevadm trigger --subsystem-match=usb --action=add 2>/dev/null || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true; fi
POSTINST
chmod 0755 "$debroot/DEBIAN/postinst"
dpkg-deb --root-owner-group --build "$debroot" "$out/hdmi-captain_${version}_amd64.deb" > /dev/null

if command -v rpmbuild >/dev/null 2>&1; then
  echo "rpm"
  rpmroot="$work/rpm"
  stage "$rpmroot/root"
  mkdir -p "$rpmroot/SPECS"
  cat > "$rpmroot/SPECS/hdmi-captain.spec" <<SPEC
Name: hdmi-captain
Version: $version
Release: 1
Summary: Low-latency viewer for USB HDMI capture cards
License: Proprietary
Requires: gtk4 >= 4.12, libadwaita >= 1.4, turbojpeg, alsa-lib, pulseaudio-libs
%description
Shows a UVC HDMI capture card with its sound and recovers by itself when it drops out.
%install
cp -a $rpmroot/root/. %{buildroot}/
%files
/opt/hdmi-captain
/usr/bin/hdmi-captain
/usr/share/applications/com.example.HDMICaptain.desktop
/usr/share/metainfo/com.example.HDMICaptain.metainfo.xml
/usr/lib/udev/rules.d/60-hdmicaptain.rules
/usr/share/icons/hicolor/*/apps/com.example.HDMICaptain.png
%post
udevadm control --reload >/dev/null 2>&1 || :
SPEC
  rpmbuild --define "_topdir $rpmroot" --define "_rpmdir $out" -bb "$rpmroot/SPECS/hdmi-captain.spec" > /dev/null
else
  echo "rpm: skipped (no rpmbuild)"
fi
rm -rf "$work"
ls -la "$out"
