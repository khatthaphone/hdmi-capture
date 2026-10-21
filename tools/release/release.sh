#!/bin/zsh
# Mac release: archive → export → (notarize + staple) → DMG → verify.
#
#   tools/release/release.sh                 # development-signed DMG (no Developer account needed)
#   DEVELOPER_ID=1 NOTARY_PROFILE=hdmicaptain tools/release/release.sh
#                                            # Developer ID export + notarization (needs the paid account;
#                                            # create the profile once with `xcrun notarytool store-credentials`)
#
# Signing identity and team come from Config/Local.xcconfig (gitignored); nothing secret is in this script.
set -euo pipefail
cd "${0:A:h}/../.."

OUT=.build/release
ARCHIVE=$OUT/HDMICaptain.xcarchive
EXPORT=$OUT/export
VERSION=$(grep -m1 'MARKETING_VERSION' project.yml | sed -E 's/.*"([^"]+)".*/\1/')
DMG=$OUT/HDMI-Captain-$VERSION.dmg
METHOD=$([[ -n "${DEVELOPER_ID:-}" ]] && echo developer-id || echo debugging)

rm -rf "$OUT" && mkdir -p "$OUT"
xcodegen generate --quiet

echo "▸ Archive (Release, arm64)"
xcodebuild -project HDMICaptain.xcodeproj -scheme HDMICaptain -configuration Release \
  -archivePath "$ARCHIVE" -derivedDataPath .build/xcode-release -allowProvisioningUpdates -quiet archive

echo "▸ Export ($METHOD)"
cat > "$OUT/ExportOptions.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>$METHOD</string>
  <key>signingStyle</key><string>automatic</string>
</dict></plist>
PLIST
xcodebuild -exportArchive -archivePath "$ARCHIVE" -exportPath "$EXPORT" \
  -exportOptionsPlist "$OUT/ExportOptions.plist" -allowProvisioningUpdates -quiet
APP="$EXPORT/HDMI Captain.app"

if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  echo "▸ Notarize"
  ditto -c -k --keepParent "$APP" "$OUT/notarize.zip"
  xcrun notarytool submit "$OUT/notarize.zip" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
fi

echo "▸ DMG"
STAGE=$OUT/dmg && mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/" && ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "HDMI Captain" -srcfolder "$STAGE" -ov -format UDZO "$DMG" -quiet
[[ -n "${NOTARY_PROFILE:-}" ]] && xcrun stapler staple "$DMG"

echo "▸ Verify"
codesign --verify --deep --strict "$APP" && echo "codesign: OK"
spctl -a -vv -t exec "$APP" 2>&1 | sed 's/^/spctl: /' || true
echo "✔ $DMG"
[[ -z "${NOTARY_PROFILE:-}" ]] && echo "  (not notarized: set DEVELOPER_ID=1 NOTARY_PROFILE=… once the Developer account exists)"
exit 0
