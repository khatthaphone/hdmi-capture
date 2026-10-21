#!/usr/bin/env bash
# Builds the Play release bundle (AAB) for HDMI Captain Android and checks it.
#
#   tools/release/android.sh            bundleRelease → .build/android-release/HDMICaptain-<version>.aab
#   tools/release/android.sh --new-key  creates the upload key (once), outside the repo
#
# Signing: Play App Signing holds the app signing key; we sign uploads with an upload key kept in
# ~/.config/hdmicaptain/ (android-upload.jks + android-signing.properties). No secrets in the repo.
# GRADLE=/path/to/gradle overrides ./gradlew when the wrapper can't download its distribution.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APP="$ROOT/Apps/Android"
OUT="$ROOT/.build/android-release"
CONFIG="$HOME/.config/hdmicaptain"
export JAVA_HOME="${JAVA_HOME_21:-/Applications/Android Studio.app/Contents/jbr/Contents/Home}"
export ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
GRADLE="${GRADLE:-./gradlew}"
READELF="$ANDROID_HOME/ndk/28.2.13676358/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-readelf"

if [[ "${1:-}" == "--new-key" ]]; then
    mkdir -p "$CONFIG" && chmod 700 "$CONFIG"
    [[ -e "$CONFIG/android-upload.jks" ]] && { echo "Upload key already exists: $CONFIG/android-upload.jks"; exit 1; }
    read -r -s -p "Upload key password: " PASS && echo
    "$JAVA_HOME/bin/keytool" -genkeypair -v -keystore "$CONFIG/android-upload.jks" -alias upload -keyalg RSA -keysize 4096 \
        -validity 10000 -storepass "$PASS" -keypass "$PASS" -dname "CN=HDMI Captain Upload"
    printf 'storeFile=%s\nstorePassword=%s\nkeyAlias=upload\nkeyPassword=%s\n' "$CONFIG/android-upload.jks" "$PASS" "$PASS" \
        > "$CONFIG/android-signing.properties"
    chmod 600 "$CONFIG/android-signing.properties" "$CONFIG/android-upload.jks"
    echo "Created $CONFIG/android-upload.jks. Back it up; register it in Play Console › App signing."
    exit 0
fi

[[ -f "$CONFIG/android-signing.properties" ]] || echo "warning: no upload key ($CONFIG/android-signing.properties); the bundle will be unsigned. Run with --new-key first."

cd "$APP"
"$GRADLE" --console=plain -q :app:bundleRelease
VERSION=$(sed -n 's/.*versionName = "\(.*\)".*/\1/p' app/build.gradle.kts)
mkdir -p "$OUT"
AAB="$OUT/HDMICaptain-$VERSION.aab"
cp app/build/outputs/bundle/release/app-release.aab "$AAB"

# Play requires 16 KB-aligned native libraries for Android 15+ devices.
TMP=$(mktemp -d)
unzip -q -o "$AAB" 'base/lib/*' -d "$TMP"
bad=0
for so in "$TMP"/base/lib/*/*.so; do
    align=$("$READELF" -lW "$so" | awk '/LOAD/{print $NF}' | sort -u | tr '\n' ' ')
    [[ "$align" == "0x4000 " ]] || { echo "not 16 KB aligned: ${so#"$TMP"/} ($align)"; bad=1; }
done
rm -rf "$TMP"
[[ $bad == 0 ]] || exit 1

echo "$AAB"
"$JAVA_HOME/bin/jarsigner" -verify "$AAB" >/dev/null 2>&1 && echo "signed with the upload key" || echo "unsigned"
