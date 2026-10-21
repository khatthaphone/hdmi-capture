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
                #if DEBUG
                .task {
                    // QA: `-qaAction upgrade` opens the upgrade sheet for screenshots.
                    guard UserDefaults.standard.string(forKey: "qaAction") == "upgrade" else { return }
                    try? await Task.sleep(for: .seconds(1.5))
                    store.requestUpgrade(highlighting: .recording)
                }
                #endif
                .sheet(isPresented: $app.isShowingHelp) { HelpSheet() }
                .sheet(isPresented: $store.isShowingUpgrade) {
                    UpgradeView().environment(store)
                }
                .sheet(isPresented: $app.isShowingStreamSetup) {
                    StreamSetupView().environment(app)
                }
                .sheet(isPresented: $app.isShowingLatencyTest) {
                    LatencyTestSheet().environment(app)
                }
        }
        .defaultSize(width: 1280, height: 720)
        .windowStyle(.hiddenTitleBar)
        .commands {
            CommandGroup(replacing: .help) {
                Button("HDMI Captain Help") { app.isShowingHelp = true }
            }
            #if DIRECT_DISTRIBUTION
            UpdateCommands()
            #endif
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
                SettingsView(page: .input)
                    .tabItem { Label("Input", systemImage: Symbol.input) }
                SettingsView(page: .picture)
                    .tabItem { Label("Picture", systemImage: Symbol.pictureLook) }
                SettingsView(page: .sound)
                    .tabItem { Label("Sound", systemImage: Symbol.sound) }
                SettingsView(page: .window)
                    .tabItem { Label("Window", systemImage: "macwindow") }
                ProfilesSettingsView()
                    .tabItem { Label("Profiles", systemImage: Symbol.profiles) }
                ProSettingsView()
                    .tabItem { Label("Pro", systemImage: Symbol.upgrade) }
            }
            .environment(app)
            .environment(store)
        }
    }
}

/// Owns one window's (or tile's) `ViewerModel` and ties it to the view lifecycle.
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
        Menu {
            ForEach(app.catalog.videoDevices) { device in
                Button(device.name) { openWindow(id: "card", value: device.id) }
            }
        } label: {
            Label("New Window for", systemImage: Symbol.newWindow)
        }
        .disabled(app.catalog.videoDevices.isEmpty)
        Button { openWindow(id: "grid") } label: { Label("Multi-View", systemImage: Symbol.multiView) }
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
        Menu {
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
                    viewer.showToast("Profile “\(profile.name)” saved")
                }
            }
            .disabled(viewer.session == nil)
            Button("Manage Profiles…") { openSettings() }
        } label: {
            Label("Profiles", systemImage: Symbol.profiles)
        }
        .disabled(viewer.session == nil)
        Button { viewer.toggleFullScreen() } label: { Label("Toggle Full Screen", systemImage: Symbol.fullScreen) }
            .keyboardShortcut("f")
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
        Divider()
        Toggle(isOn: $app.prefs.floatOnTop) { Label("Float on Top", systemImage: Symbol.floatOnTop) }
            .keyboardShortcut("t")
        Toggle(isOn: $app.prefs.allSpaces) { Label("Show on All Spaces", systemImage: Symbol.allSpaces) }
        Toggle(isOn: $app.prefs.cleanMode) { Label("Clean Mode", systemImage: Symbol.cleanMode) }
            .keyboardShortcut("b")
        Toggle(isOn: $app.prefs.lockAspect) { Label("Lock Aspect Ratio", systemImage: Symbol.lockAspect) }
        Toggle(isOn: $app.prefs.showStats) { Label("Show Diagnostics", systemImage: Symbol.stats) }
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
        Button { openWindow(id: "scopes") } label: { Label("Show Scopes", systemImage: Symbol.scopes) }
            .keyboardShortcut("s", modifiers: [.command, .option])
        Button { app.isShowingLatencyTest = true } label: { Label("Latency Test…", systemImage: "stopwatch") }
        Menu { GuideItems(app: app) } label: { Label("Guides", systemImage: Symbol.guides) }
        Divider()
        if viewer.source == .automatic {
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
        }
        Button { app.retry() } label: { Label("Reconnect", systemImage: Symbol.reconnect) }
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
            Button { app.showRecordingsInFinder() } label: { Label("Show Recordings in Finder", systemImage: Symbol.showRecordings) }
            Divider()
            Toggle(isOn: Binding(
                get: { app.prefs.recentEnabled && store.isUnlocked(.replayBuffer) },
                set: { on in if !on || store.gate(.replayBuffer) { app.prefs.recentEnabled = on } }
            )) {
                Label("Keep Recent Footage (\(app.prefs.recentMinutes) min)",
                      systemImage: store.isUnlocked(.replayBuffer) ? Symbol.keepRecent : Symbol.locked)
            }
            SaveRecentMenu(app: app, store: store)
            Divider()
            if ProFeature.shown.contains(.streaming) {
                item(.streaming, app.streamer.state.isActive ? "Stop Streaming" : "Go Live…", key: "l", modifiers: [.command, .shift])
            }
            if ProFeature.shown.contains(.virtualCamera) {
                item(.virtualCamera, "Virtual Camera", key: nil, modifiers: [])
            }
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
            case .replayBuffer: app.currentViewer?.session?.saveRecent(seconds: 30)
            case .streaming: app.streamer.state.isActive ? app.stopStreaming() : (app.isShowingStreamSetup = true)
            default: break
            }
        } label: {
            Label(unlocked && !built ? "\(title) (coming soon)" : title,
                  systemImage: unlocked ? feature.symbol : Symbol.locked)
        }
        .disabled(unlocked && !built)
        if let key {
            button.keyboardShortcut(key, modifiers: modifiers)
        } else {
            button
        }
    }
}

/// Pro → Save Recent ▸ Last 15 Seconds … Everything (PlayStation "Save recent gameplay" style).
private struct SaveRecentMenu: View {
    let app: AppModel
    let store: EntitlementStore

    var body: some View {
        Menu {
            SaveRecentItems(app: app, store: store, viewer: app.currentViewer, shortcuts: true)
        } label: {
            Label("Save Recent", systemImage: store.isUnlocked(.replayBuffer) ? Symbol.saveRecent : Symbol.locked)
        }
    }
}
