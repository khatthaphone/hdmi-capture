DERIVED := .build/xcode
APP := $(DERIVED)/Build/Products/Debug/HDMI Captain.app

.PHONY: gen build run test test-app build-ios-core test-ios-core inventory logs clean

IOS_SIM ?= iPad Pro 11-inch (M5)

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

inventory:
	cd tools/device-inventory && swift run -q device-inventory

logs:
	log stream --level debug --predicate 'subsystem == "hdmicaptain"'

clean:
	rm -rf .build HDMICaptain.xcodeproj
