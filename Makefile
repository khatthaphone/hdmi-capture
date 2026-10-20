DERIVED := .build/xcode
APP := $(DERIVED)/Build/Products/Debug/HDMI Captain.app

.PHONY: gen build run test inventory logs clean

gen:
	xcodegen generate --quiet

build: gen
	xcodebuild -project HDMICaptain.xcodeproj -scheme HDMICaptain -configuration Debug -derivedDataPath $(DERIVED) -quiet build

run: build
	open "$(APP)"

test:
	cd Packages/CaptureCore && swift test

inventory:
	cd tools/device-inventory && swift run -q device-inventory

logs:
	log stream --level debug --predicate 'subsystem == "hdmicaptain"'

clean:
	rm -rf .build HDMICaptain.xcodeproj
