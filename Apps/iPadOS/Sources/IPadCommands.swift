import CaptureCore
import SwiftUI

/// iPadOS menu bar (swipe down from the top, or hold ⌘ for the shortcut list).
/// Same items and shortcuts as the Mac where they make sense; window-only items are left out.
struct IPadCommands: Commands {
    let app: AppModel
    let store: EntitlementStore

    var body: some Commands {
        CommandGroup(replacing: .appSettings) {
            Button { app.isShowingSettings = true } label: { Label("Settings…", systemImage: Symbol.settings) }
                .keyboardShortcut(",")
        }
        CommandMenu("Video") {
            if let viewer = app.currentViewer {
                VideoItems(app: app, viewer: viewer)
            }
        }
        CommandMenu("Pro") {
            ProItems(app: app, store: store)
        }
    }
}

private struct VideoItems: View {
    @Bindable var app: AppModel
    @Bindable var viewer: ViewerModel

    var body: some View {
        Button { viewer.saveScreenshot() } label: { Label("Save Screenshot", systemImage: Symbol.screenshot) }
            .keyboardShortcut("s", modifiers: [.command, .shift])
        Button { viewer.copyFrame() } label: { Label("Copy Frame", systemImage: Symbol.copyFrame) }
            .keyboardShortcut("c", modifiers: [.command, .option])
        Toggle(isOn: $viewer.preset.muted) { Label("Mute", systemImage: Symbol.mute) }
            .keyboardShortcut("m", modifiers: [.command, .shift])
        Divider()
        ForEach(Array(ScaleMode.allCases.enumerated()), id: \.element) { index, mode in
            Toggle(isOn: Binding(
                get: { viewer.preset.scale == mode },
                set: { _ in viewer.preset.scale = mode }
            )) {
                Label(mode.title, systemImage: mode.symbol)
            }
            .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")))
        }
        Menu { PictureLookItems(viewer: viewer) } label: { Label("Picture Look", systemImage: Symbol.pictureLook) }
        Toggle(isOn: Binding(
            get: { viewer.preset.filter == .nearest },
            set: { viewer.preset.filter = $0 ? .nearest : .linear }
        )) {
            Label("Sharp Pixels", systemImage: Symbol.sharpPixels)
        }
        Menu { GuideItems(app: app) } label: { Label("Guides", systemImage: Symbol.guides) }
        Toggle(isOn: $app.prefs.showStats) { Label("Show Stats", systemImage: Symbol.stats) }
            .keyboardShortcut("i")
        Divider()
        Menu {
            Toggle("Off (live picture)", isOn: Binding(get: { viewer.testPattern == nil }, set: { _ in viewer.testPattern = nil }))
            Divider()
            ForEach(TestPattern.allCases, id: \.self) { pattern in
                Toggle(pattern.title, isOn: Binding(
                    get: { viewer.testPattern == pattern },
                    set: { viewer.testPattern = $0 ? pattern : nil }
                ))
            }
        } label: {
            Label("Test Pattern", systemImage: Symbol.testPattern)
        }
        Menu {
            ForEach(app.catalog.videoDevices) { device in
                Toggle(device.name, isOn: Binding(
                    get: { app.mainDeviceID == device.id },
                    set: { _ in app.selectVideo(device.id) }
                ))
            }
        } label: {
            Label("Video Input", systemImage: Symbol.input)
        }
        Button { app.retry() } label: { Label("Reconnect", systemImage: Symbol.reconnect) }
            .keyboardShortcut("r", modifiers: [.command, .option])
    }
}

private struct ProItems: View {
    let app: AppModel
    let store: EntitlementStore

    var body: some View {
        Button {
            guard store.gate(.recording) else { return }
            app.currentViewer?.toggleRecording()
        } label: {
            let recording = app.currentViewer?.session?.isRecording == true
            Label(recording ? "Stop Recording" : "Record",
                  systemImage: store.isUnlocked(.recording) ? Symbol.record : Symbol.locked)
        }
        .keyboardShortcut("r")
        Menu {
            SaveRecentItems(app: app, store: store, viewer: app.currentViewer, shortcuts: true)
        } label: {
            Label("Save Recent", systemImage: store.isUnlocked(.replayBuffer) ? Symbol.saveRecent : Symbol.locked)
        }
        Divider()
        Button(store.isPro ? "HDMI Captain Pro ✓" : "Upgrade to Pro…") { store.requestUpgrade() }
    }
}
