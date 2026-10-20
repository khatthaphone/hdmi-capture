import AppKit
import CaptureCore
import SwiftUI

@main
struct HDMICaptainApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var app = AppModel()
    @State private var store = EntitlementStore()

    init() {
        let app = _app.wrappedValue
        AppDelegate.keepsRunningWithoutWindow = { app.keepsRunningWithoutWindow }
        DispatchQueue.main.async { app.applyActivationPolicy() }
    }

    var body: some Scene {
        Window("HDMI Captain", id: "main") {
            ViewerRoot(app: app, source: .automatic)
                .environment(app)
                .environment(store)
                .sheet(isPresented: $store.isShowingUpgrade) {
                    UpgradeView().environment(store)
                }
                .sheet(isPresented: $app.isShowingStreamSetup) {
                    StreamSetupView().environment(app)
                }
        }
        .defaultSize(width: 1280, height: 720)
        .windowStyle(.hiddenTitleBar)
        .commands {
            CardWindowCommands(app: app)
            ViewerCommands(app: app)
            WindowSizeCommands(app: app)
            ProCommands(app: app, store: store)
        }

        // One window per capture card (File → New Window for…). Reopening a card focuses its window.
        WindowGroup("Capture Card", id: "card", for: String.self) { $deviceID in
            if let deviceID {
                ViewerRoot(app: app, source: .pinned(deviceID))
                    .environment(app)
                    .environment(store)
            }
        }
        .defaultSize(width: 960, height: 540)
        .windowStyle(.hiddenTitleBar)

        Window("Scopes", id: "scopes") {
            ScopesPanel()
                .environment(app)
        }
        .defaultSize(width: 900, height: 300)

        Window("Multi-View", id: "grid") {
            MultiViewGrid()
                .environment(app)
                .environment(store)
        }
        .defaultSize(width: 1280, height: 720)

        MenuBarExtra("HDMI Captain", systemImage: "rectangle.inset.filled.on.rectangle", isInserted: $app.prefs.showMenuBarItem) {
            MenuBarPanel()
                .environment(app)
                .environment(store)
        }
        .menuBarExtraStyle(.window)

        Settings {
            TabView {
                SettingsView()
                    .tabItem { Label("General", systemImage: "gearshape") }
                ProfilesSettingsView()
                    .tabItem { Label("Profiles", systemImage: "person.crop.rectangle.stack") }
                ProSettingsView()
                    .tabItem { Label("Pro", systemImage: "sparkles") }
            }
            .environment(app)
            .environment(store)
        }
    }
}

/// Owns one window's (or tile's) `ViewerModel` and ties it to the view lifecycle.
struct ViewerRoot: View {
    @State private var viewer: ViewerModel

    init(app: AppModel, source: ViewerModel.Source) {
        _viewer = State(initialValue: ViewerModel(app: app, source: source))
    }

    var body: some View {
        ContentView()
            .environment(viewer)
            .onAppear { viewer.activate() }
            .onDisappear { viewer.deactivate() }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// Set by the app once the model exists.
    @MainActor static var keepsRunningWithoutWindow: () -> Bool = { false }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        MainActor.assumeIsolated { !Self.keepsRunningWithoutWindow() }
    }
}

// MARK: - Commands

/// File → New Window for each connected card.
struct CardWindowCommands: Commands {
    let app: AppModel

    var body: some Commands {
        CommandGroup(after: .newItem) {
            CardWindowMenu(app: app)
        }
    }
}

private struct CardWindowMenu: View {
    let app: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Menu("New Window for") {
            ForEach(app.catalog.videoDevices) { device in
                Button(device.name) { openWindow(id: "card", value: device.id) }
            }
        }
        .disabled(app.catalog.videoDevices.isEmpty)
        Button("Multi-View") { openWindow(id: "grid") }
            .keyboardShortcut("g", modifiers: [.command, .option])
    }
}

struct WindowSizeCommands: Commands {
    let app: AppModel

    var body: some Commands {
        CommandGroup(after: .windowSize) {
            Divider()
            Button("Actual Pixels (1:1)") { app.currentViewer?.setWindowSize(scale: 1, actualPixels: true) }
                .keyboardShortcut("0")
            ForEach([0.5, 1, 1.5, 2], id: \.self) { scale in
                Button("\(Int(scale * 100)) % Size") { app.currentViewer?.setWindowSize(scale: scale) }
            }
            Divider()
        }
    }
}

/// Video menu; acts on the key window's viewer.
struct ViewerCommands: Commands {
    let app: AppModel

    var body: some Commands {
        CommandMenu("Video") {
            if let viewer = app.currentViewer {
                VideoMenuItems(app: app, viewer: viewer)
            }
        }
    }
}

private struct VideoMenuItems: View {
    @Bindable var app: AppModel
    @Bindable var viewer: ViewerModel
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        Menu("Profiles") {
            ForEach(Array(app.profiles.enumerated()), id: \.element.id) { index, profile in
                let toggle = Toggle(isOn: Binding(
                    get: { viewer.preset.profileID == profile.id },
                    set: { _ in viewer.session?.applyProfile(profile) }
                )) {
                    Label(profile.name, systemImage: profile.symbol)
                }
                if index < 9 {
                    toggle.keyboardShortcut(KeyEquivalent(Character("\(index + 1)")), modifiers: [.command, .option])
                } else {
                    toggle
                }
            }
            Divider()
            Button("Save Current as Profile") {
                if let session = viewer.session {
                    let profile = app.saveProfile(from: session)
                    viewer.showToast("Saved profile \(profile.name)")
                }
            }
            .disabled(viewer.session == nil)
            Button("Manage Profiles…") { openSettings() }
        }
        .disabled(viewer.session == nil)
        Button("Toggle Full Screen") { viewer.toggleFullScreen() }
            .keyboardShortcut("f")
        Button("Save Screenshot") { viewer.saveScreenshot() }
            .keyboardShortcut("s", modifiers: [.command, .shift])
        Button("Copy Frame") { viewer.copyFrame() }
            .keyboardShortcut("c", modifiers: [.command, .option])
        Toggle("Mute", isOn: $viewer.preset.muted)
            .keyboardShortcut("m", modifiers: [.command, .shift])
        Divider()
        ForEach(Array(ScaleMode.allCases.enumerated()), id: \.element) { index, mode in
            Toggle(mode.title, isOn: Binding(
                get: { viewer.preset.scale == mode },
                set: { _ in viewer.preset.scale = mode }
            ))
            .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")))
        }
        Menu("Picture Look") {
            ForEach(ImageAdjustments.Preset.allCases, id: \.self) { preset in
                Toggle(preset.title, isOn: Binding(
                    get: { viewer.preset.adjustments == preset.values },
                    set: { _ in viewer.preset.adjustments = preset.values }
                ))
            }
        }
        Toggle("Sharp Pixels", isOn: Binding(
            get: { viewer.preset.filter == .nearest },
            set: { viewer.preset.filter = $0 ? .nearest : .linear }
        ))
        Divider()
        Toggle("Float on Top", isOn: $app.prefs.floatOnTop)
            .keyboardShortcut("t")
        Toggle("Show on All Spaces", isOn: $app.prefs.allSpaces)
        Toggle("Clean Mode", isOn: $app.prefs.cleanMode)
            .keyboardShortcut("b")
        Toggle("Lock Aspect Ratio", isOn: $app.prefs.lockAspect)
        Toggle("Show Stats", isOn: $app.prefs.showStats)
            .keyboardShortcut("i")
        Divider()
        Menu("Test Pattern") {
            Toggle("Off (live picture)", isOn: Binding(get: { viewer.testPattern == nil }, set: { _ in viewer.testPattern = nil }))
            Divider()
            ForEach(TestPattern.allCases, id: \.self) { pattern in
                Toggle(pattern.title, isOn: Binding(
                    get: { viewer.testPattern == pattern },
                    set: { viewer.testPattern = $0 ? pattern : nil }
                ))
            }
        }
        Button("Show Scopes") { openWindow(id: "scopes") }
            .keyboardShortcut("s", modifiers: [.command, .option])
        Menu("Guides") {
            Toggle("Rule of Thirds", isOn: $app.prefs.guides.thirds)
            Toggle("Center Cross", isOn: $app.prefs.guides.center)
            Toggle("Safe Areas (90 % / 80 %)", isOn: $app.prefs.guides.safeAreas)
            Divider()
            Toggle("No Aspect Mask", isOn: Binding(get: { app.prefs.guides.mask == nil }, set: { _ in app.prefs.guides.mask = nil }))
            ForEach(GuideSettings.Mask.allCases, id: \.self) { mask in
                Toggle("Mask \(mask.rawValue)", isOn: Binding(
                    get: { app.prefs.guides.mask == mask },
                    set: { app.prefs.guides.mask = $0 ? mask : nil }
                ))
            }
        }
        Divider()
        if viewer.source == .automatic {
            Menu("Video Input") {
                ForEach(app.catalog.videoDevices) { device in
                    Toggle(device.name, isOn: Binding(
                        get: { app.mainDeviceID == device.id },
                        set: { _ in app.selectVideo(device.id) }
                    ))
                }
            }
        }
        Button("Reconnect") { app.retry() }
            .keyboardShortcut("r", modifiers: [.command, .option])
    }
}

/// Pro actions: locked items open the upgrade sheet; unlocked-but-unbuilt items are disabled.
struct ProCommands: Commands {
    let app: AppModel
    let store: EntitlementStore

    var body: some Commands {
        CommandMenu("Pro") {
            let recording = app.currentViewer?.session?.isRecording ?? false
            item(.recording, recording ? "Stop Recording" : "Start Recording", key: "r", modifiers: .command)
            Button("Show Recordings in Finder") { app.showRecordingsInFinder() }
            Toggle(isOn: Binding(
                get: { app.prefs.replayEnabled && store.isUnlocked(.replayBuffer) },
                set: { on in if !on || store.gate(.replayBuffer) { app.prefs.replayEnabled = on } }
            )) {
                Label("Instant Replay Buffer (\(Int(app.prefs.replaySeconds)) s)",
                      systemImage: store.isUnlocked(.replayBuffer) ? "gobackward" : "lock.fill")
            }
            item(.replayBuffer, "Save Instant Replay", key: "r", modifiers: [.command, .shift])
            item(.streaming, app.streamer.state.isActive ? "Stop Streaming" : "Go Live…", key: "l", modifiers: [.command, .shift])
            item(.virtualCamera, "Virtual Camera", key: nil, modifiers: [])
            Divider()
            Button(store.isPro ? "HDMI Captain Pro ✓" : "Upgrade to Pro…") {
                store.requestUpgrade()
            }
        }
    }

    @ViewBuilder
    private func item(_ feature: ProFeature, _ title: String, key: KeyEquivalent?, modifiers: EventModifiers) -> some View {
        let unlocked = store.isUnlocked(feature)
        let built = ProFeature.available.contains(feature)
        let button = Button {
            guard store.gate(feature) else { return }
            switch feature {
            case .recording: app.currentViewer?.toggleRecording()
            case .replayBuffer: app.currentViewer?.session?.saveReplay()
            case .streaming: app.streamer.state.isActive ? app.stopStreaming() : (app.isShowingStreamSetup = true)
            default: break
            }
        } label: {
            Label(unlocked && !built ? "\(title) (coming soon)" : title,
                  systemImage: unlocked ? feature.symbol : "lock.fill")
        }
        .disabled(unlocked && !built)
        if let key {
            button.keyboardShortcut(key, modifiers: modifiers)
        } else {
            button
        }
    }
}
