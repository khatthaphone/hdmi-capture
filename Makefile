DERIVED := .build/xcode
APP := $(DERIVED)/Build/Products/Debug/HDMI Captain.app

.PHONY: gen build run test test-app build-ios-core test-ios-core build-ipad run-ipad-sim run-ipad ipad-snapshot ipad-soak inventory logs clean

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
	for f in qa-soak.csv qa-formats.txt; do xcrun devicectl device copy from --device $(IPAD_ID) --domain-type appDataContainer --domain-identifier com.example.hdmicaptain --source Documents/$$f --destination $(SNAP_DIR)/$$f -q || true; done
	@cat $(SNAP_DIR)/qa-soak.csv

inventory:
	cd tools/device-inventory && swift run -q device-inventory

logs:
	log stream --level debug --predicate 'subsystem == "hdmicaptain"'

clean:
	rm -rf .build HDMICaptain.xcodeproj
