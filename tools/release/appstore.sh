#!/bin/zsh
# App Store builds for Mac and iPad: archive both, then upload to App Store Connect (TestFlight).
#   tools/release/appstore.sh            # archive only (works without the Developer account)
#   UPLOAD=1 tools/release/appstore.sh   # archive + upload (needs the paid account and the app record)
set -euo pipefail
cd "${0:A:h}/../.."
OUT=.build/appstore
rm -rf "$OUT" && mkdir -p "$OUT"
xcodegen generate --quiet

archive() { # scheme destination name
  echo "▸ Archive $3"
  xcodebuild -project HDMICaptain.xcodeproj -scheme "$1" -configuration Release -destination "$2" \
    -archivePath "$OUT/$3.xcarchive" -derivedDataPath .build/xcode-appstore -allowProvisioningUpdates -quiet archive
}
archive HDMICaptain 'generic/platform=macOS' mac
archive HDMICaptain-iPad 'generic/platform=iOS' ipad

if [[ -n "${UPLOAD:-}" ]]; then
  cat > "$OUT/ExportOptions.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>app-store-connect</string>
  <key>destination</key><string>upload</string>
  <key>signingStyle</key><string>automatic</string>
</dict></plist>
PLIST
  for name in mac ipad; do
    echo "▸ Upload $name"
    xcodebuild -exportArchive -archivePath "$OUT/$name.xcarchive" -exportPath "$OUT/$name" \
      -exportOptionsPlist "$OUT/ExportOptions.plist" -allowProvisioningUpdates
  done
fi
ls -d "$OUT"/*.xcarchive
