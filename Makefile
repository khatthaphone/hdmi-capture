DERIVED := .build/xcode
APP := $(DERIVED)/Build/Products/Debug/HDMI Captain.app

.PHONY: gen build run test test-app build-ios-core test-ios-core build-ipad run-ipad-sim run-ipad run-ipad-profile install-mac ipad-snapshot ipad-soak android-build android-test android-run android-check16k android-soak win-deps win-build win-test win-run win-package win-cert win-vcam inventory logs clean

IOS_SIM ?= iPad Pro 11-inch (M5)
IPAD_APP_SIM := $(DERIVED)/Build/Products/Debug-iphonesimulator/HDMI Captain.app
IPAD_APP := $(DERIVED)/Build/Products/Debug-iphoneos/HDMI Captain.app
# First paired iPad (override with IPAD_ID=<devicectl identifier>)
IPAD_ID ?= $(shell xcrun devicectl list devices 2>/dev/null | awk '/iPad/ && (/available/ || /connected/) {for (i=1;i<=NF;i++) if ($$i ~ /^[0-9A-F-]{36}$$/) print $$i}' | head -1)
IPAD_ARGS ?=

gen:
	xcodegen generate --quiet

build: gen
	xcodebuild -project HDMICaptain.xcodeproj -scheme HDMICaptain -configuration Debug -derivedDataPath $(DERIVED) -quiet build

run: build
	open "$(APP)"

# Release Mac build with the developer Pro toggle (ALLOW_DEBUG_PRO), copied to /Applications
MAC_PROFILE_DERIVED := .build/xcode-mac-profile
install-mac: gen
	xcodebuild -project HDMICaptain.xcodeproj -scheme HDMICaptain -configuration Release -derivedDataPath $(MAC_PROFILE_DERIVED) -quiet build 'SWIFT_ACTIVE_COMPILATION_CONDITIONS=$$(inherited) ALLOW_DEBUG_PRO'
	-pkill -x "HDMI Captain"
	rm -rf "/Applications/HDMI Captain.app"
	ditto "$(MAC_PROFILE_DERIVED)/Build/Products/Release/HDMI Captain.app" "/Applications/HDMI Captain.app"
	open "/Applications/HDMI Captain.app" --args $(MAC_ARGS)

test:
	cd Packages/CaptureCore && swift test

# App-hosted tests (StoreKit testing against Apps/macOS/HDMICaptain.storekit)
test-app: gen
	xcodebuild -project HDMICaptain.xcodeproj -scheme HDMICaptain -configuration Debug -derivedDataPath $(DERIVED) -quiet -only-testing:HDMICaptainTests test

# CaptureCore for iOS (iPad app, Phase 3)
build-ios-core:
	cd Packages/CaptureCore && xcodebuild -scheme CaptureCore -destination 'generic/platform=iOS' -derivedDataPath ../../$(DERIVED)-ios -quiet build

test-ios-core:
	cd Packages/CaptureCore && xcodebuild -scheme CaptureCore -destination 'platform=iOS Simulator,name=$(IOS_SIM)' -derivedDataPath ../../$(DERIVED)-ios -quiet test

# iPad app (Phase 3). ARGS go to the app, e.g. IPAD_ARGS="-testPattern smpteBars -qaShowStats YES"
build-ipad: gen
	xcodebuild -project HDMICaptain.xcodeproj -scheme HDMICaptain-iPad -configuration Debug -destination 'platform=iOS Simulator,name=$(IOS_SIM)' -derivedDataPath $(DERIVED) -quiet build

run-ipad-sim: build-ipad
	xcrun simctl boot '$(IOS_SIM)' 2>/dev/null || true
	open -a Simulator
	xcrun simctl install booted "$(IPAD_APP_SIM)"
	xcrun simctl launch --terminate-running-process booted com.example.hdmicaptain -- $(IPAD_ARGS)

run-ipad: gen
	@test -n "$(IPAD_ID)" || (echo "No paired iPad found (xcrun devicectl list devices)"; exit 1)
	xcodebuild -project HDMICaptain.xcodeproj -scheme HDMICaptain-iPad -configuration Debug -destination 'id=$(IPAD_ID)' -derivedDataPath $(DERIVED) -allowProvisioningUpdates -quiet build
	xcrun devicectl device install app --device $(IPAD_ID) "$(IPAD_APP)"
	xcrun devicectl device process launch --terminate-existing --device $(IPAD_ID) com.example.hdmicaptain -- $(IPAD_ARGS)

# Release ("Profile") build on the iPad, as users get it, but with the developer Pro unlock compiled
# in (ALLOW_DEBUG_PRO) so `IPAD_ARGS="-debugUnlockPro YES"` works. Never used for App Store builds.
IPAD_PROFILE_DERIVED := .build/xcode-ipad-profile
run-ipad-profile: gen
	@test -n "$(IPAD_ID)" || (echo "No paired iPad found (xcrun devicectl list devices)"; exit 1)
	xcodebuild -project HDMICaptain.xcodeproj -scheme HDMICaptain-iPad -configuration Release -destination 'id=$(IPAD_ID)' -derivedDataPath $(IPAD_PROFILE_DERIVED) -allowProvisioningUpdates -quiet build 'SWIFT_ACTIVE_COMPILATION_CONDITIONS=$$(inherited) ALLOW_DEBUG_PRO'
	xcrun devicectl device install app --device $(IPAD_ID) "$(IPAD_PROFILE_DERIVED)/Build/Products/Release-iphoneos/HDMI Captain.app"
	xcrun devicectl device process launch --terminate-existing --device $(IPAD_ID) com.example.hdmicaptain -- $(IPAD_ARGS)

# DEBUG builds: saves the window + raw frame on the iPad and copies them to SNAP_DIR.
SNAP_DIR ?= .build/ipad-snapshot
ipad-snapshot:
	@mkdir -p $(SNAP_DIR)
	xcrun devicectl device notification post --device $(IPAD_ID) --name com.example.hdmicaptain.qa.snapshot -q
	@sleep 2
	for f in qa-ui.png qa-frame.png; do xcrun devicectl device copy from --device $(IPAD_ID) --domain-type appDataContainer --domain-identifier com.example.hdmicaptain --source Documents/$$f --destination $(SNAP_DIR)/$$f -q || true; done
	@ls -la $(SNAP_DIR)

# DEBUG builds launched with IPAD_ARGS="-qaSoakMinutes 30": copies the soak CSV and format list.
ipad-soak:
	@mkdir -p $(SNAP_DIR)
	for f in qa-soak.csv qa-formats.txt qa-events.csv; do xcrun devicectl device copy from --device $(IPAD_ID) --domain-type appDataContainer --domain-identifier com.example.hdmicaptain --source Documents/$$f --destination $(SNAP_DIR)/$$f -q || true; done
	@cat $(SNAP_DIR)/qa-soak.csv

# Android (Apps/Android). JDK 21 from Android Studio; the system JDK is too new for AGP.
# ANDROID_ARGS are intent extras, e.g. ANDROID_ARGS="--es testPattern smpteBars".
# GRADLE can point at a local Gradle when the wrapper can't download its distribution.
ANDROID_DIR := Apps/Android
ANDROID_ENV := JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home" ANDROID_HOME="$(HOME)/Library/Android/sdk"
GRADLE ?= ./gradlew
ANDROID_ARGS ?=

android-build:
	cd $(ANDROID_DIR) && $(ANDROID_ENV) $(GRADLE) assembleDebug

android-test:
	cd $(ANDROID_DIR) && $(ANDROID_ENV) $(GRADLE) test

# Every native library in the APK must have 16 KB-aligned LOAD segments (Play, Android 15+).
android-check16k:
	@rm -rf .build/apk-libs && unzip -q -o $(ANDROID_DIR)/app/build/outputs/apk/debug/app-debug.apk 'lib/*' -d .build/apk-libs
	@bad=0; for f in .build/apk-libs/lib/*/*.so; do \
	  a=$$($(HOME)/Library/Android/sdk/ndk/28.2.13676358/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-readelf -lW $$f | awk '/LOAD/{print $$NF}' | sort -u | tr '\n' ' '); \
	  echo "$$f $$a"; case "$$a" in "0x4000 ") ;; *) bad=1;; esac; done; exit $$bad

# Copies the DEBUG soak CSV, descriptor and Camera2 probes, and the QA frame from the app's files.
android-soak:
	@mkdir -p .build/android-qa && for f in qa-soak.csv qa-descriptors.json qa-camera2.json qa-frame.png; do \
	  adb exec-out run-as com.example.hdmicaptain cat files/$$f > .build/android-qa/$$f 2>/dev/null && echo ".build/android-qa/$$f"; done

android-run: android-build
	adb install -r $(ANDROID_DIR)/app/build/outputs/apk/debug/app-debug.apk
	adb shell am start -S -n com.example.hdmicaptain/.MainActivity $(ANDROID_ARGS)

# Windows (Apps/Windows), run from Git Bash/GNU make on the Windows dev PC with the .NET 10 SDK.
# WIN_ARGS are the Apple-style launch arguments, e.g. WIN_ARGS="-testPattern smpteBars".
# WIN_PLATFORM is x64 or ARM64. win-deps builds libjpeg-turbo with vcpkg (VCPKG overrides its path).
WIN_DIR := Apps/Windows
WIN_PLATFORM ?= x64
WIN_RID := $(if $(filter ARM64,$(WIN_PLATFORM)),win-arm64,win-x64)
WIN_TRIPLET := $(if $(filter ARM64,$(WIN_PLATFORM)),arm64-windows,x64-windows)
WIN_EXE := $(WIN_DIR)/src/HDMICaptain.App/bin/Debug/net10.0-windows10.0.22621.0/$(WIN_RID)/HDMICaptain.exe
WIN_CERT := $(WIN_DIR)/.local/HDMICaptainDev.pfx
VCPKG ?= C:/vcpkg/vcpkg
WIN_ARGS ?=

win-deps:
	@test -f $(WIN_DIR)/native/vcpkg_installed/$(WIN_TRIPLET)/bin/turbojpeg.dll || \
	  (cd $(WIN_DIR)/native && $(VCPKG) install --triplet $(WIN_TRIPLET))

win-build: win-deps
	cd $(WIN_DIR) && dotnet build HDMICaptain.Windows.slnx -c Debug -p:Platform=$(WIN_PLATFORM) -nologo

win-test: win-deps
	cd $(WIN_DIR) && dotnet test --solution HDMICaptain.Windows.slnx -p:Platform=$(WIN_PLATFORM)

win-run: win-build
	"$(WIN_EXE)" $(WIN_ARGS) > /dev/null 2>&1 &

# Self-signed dev certificate for sideloading the MSIX; the script prints the one-time trust command (admin).
win-cert:
	@pwsh -NoProfile -File $(WIN_DIR)/tools/new-dev-cert.ps1 -Path "$(abspath $(WIN_CERT))"

win-package: win-deps
	cd $(WIN_DIR) && dotnet publish src/HDMICaptain.App/HDMICaptain.App.csproj -c Release -p:Platform=$(WIN_PLATFORM) \
	  -p:GenerateAppxPackageOnBuild=true -p:AppxSymbolPackageEnabled=false -p:AppxPackageSigningEnabled=true \
	  -p:PackageCertificateThumbprint=$$(pwsh -NoProfile -File tools/new-dev-cert.ps1 -Path "$(abspath $(WIN_CERT))") \
	  -p:AppxPackageDir="$(abspath $(WIN_DIR))/AppPackages/" -nologo
	@ls -t $(WIN_DIR)/AppPackages/*/*.msix | head -1

# Virtual camera test source (task 104): HDMICaptainVCam.dll + vcam.exe, Release, CMake + VS 2022.
# Output in $(VCAM_BIN). Register once from an elevated prompt: regsvr32 "<VCAM_BIN>\HDMICaptainVCam.dll"
# (rebuilding fails to overwrite the DLL while Frame Server has it loaded: `vcam add` stopped, no camera app open).
VCAM_DIR := $(WIN_DIR)/native/VirtualCamera
VCAM_BIN := $(VCAM_DIR)/bin/$(WIN_PLATFORM)
CMAKE ?= $(if $(shell command -v cmake 2>/dev/null),cmake,"C:/Program Files/CMake/bin/cmake.exe")

win-vcam:
	$(CMAKE) -S $(VCAM_DIR) -B $(VCAM_DIR)/build/$(WIN_PLATFORM) -G "Visual Studio 17 2022" -A $(WIN_PLATFORM)
	$(CMAKE) --build $(VCAM_DIR)/build/$(WIN_PLATFORM) --config Release -- -nologo -v:minimal
	mkdir -p $(VCAM_BIN)
	cp $(VCAM_DIR)/build/$(WIN_PLATFORM)/out/Release/HDMICaptainVCam.dll $(VCAM_DIR)/build/$(WIN_PLATFORM)/out/Release/vcam.exe $(VCAM_BIN)/
	@echo "built $(VCAM_BIN)/HDMICaptainVCam.dll and vcam.exe; run '$(VCAM_BIN)/vcam.exe status'"

inventory:
	cd tools/device-inventory && swift run -q device-inventory

logs:
	log stream --level debug --predicate 'subsystem == "hdmicaptain"'

clean:
	rm -rf .build HDMICaptain.xcodeproj
