import AppKit
import CaptureCore
import SwiftUI

@main
struct HDMICaptainApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = AppModel()
    @State private var store = EntitlementStore()

    var body: some Scene {
        Window("HDMI Captain", id: "main") {
            ContentView()
                .environment(model)
                .environment(store)
                .sheet(isPresented: $store.isShowingUpgrade) {
                    UpgradeView().environment(store)
                }
        }
        .defaultSize(width: 1280, height: 720)
        .windowStyle(.hiddenTitleBar)
        .commands {
            ViewerCommands(model: model)
            ProCommands(store: store)
        }

        Settings {
            TabView {
                SettingsView()
                    .tabItem { Label("General", systemImage: "gearshape") }
                ProSettingsView()
                    .tabItem { Label("Pro", systemImage: "sparkles") }
            }
            .environment(model)
            .environment(store)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

struct ViewerCommands: Commands {
    @Bindable var model: AppModel

    var body: some Commands {
        CommandMenu("Video") {
            Button("Toggle Full Screen") { model.toggleFullScreen() }
                .keyboardShortcut("f")
            Button("Save Screenshot") { model.saveScreenshot() }
                .keyboardShortcut("s", modifiers: [.command, .shift])
            Button("Copy Frame") { model.copyFrame() }
                .keyboardShortcut("c", modifiers: [.command, .option])
            Toggle("Mute", isOn: $model.preset.muted)
                .keyboardShortcut("m", modifiers: [.command, .shift])
            Divider()
            ForEach(Array(ScaleMode.allCases.enumerated()), id: \.element) { index, mode in
                Toggle(mode.title, isOn: Binding(
                    get: { model.preset.scale == mode },
                    set: { _ in model.preset.scale = mode }
                ))
                .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")))
            }
            Toggle("Sharp Pixels", isOn: Binding(
                get: { model.preset.filter == .nearest },
                set: { model.preset.filter = $0 ? .nearest : .linear }
            ))
            Divider()
            Toggle("Float on Top", isOn: $model.prefs.floatOnTop)
                .keyboardShortcut("t")
            Toggle("Show on All Spaces", isOn: $model.prefs.allSpaces)
            Toggle("Clean Mode", isOn: $model.prefs.cleanMode)
                .keyboardShortcut("b")
            Toggle("Lock Aspect Ratio", isOn: $model.prefs.lockAspect)
            Toggle("Show Stats", isOn: $model.prefs.showStats)
                .keyboardShortcut("i")
            Divider()
            Menu("Test Pattern") {
                Toggle("Off (live picture)", isOn: Binding(get: { model.testPattern == nil }, set: { _ in model.testPattern = nil }))
                Divider()
                ForEach(TestPattern.allCases, id: \.self) { pattern in
                    Toggle(pattern.title, isOn: Binding(
                        get: { model.testPattern == pattern },
                        set: { model.testPattern = $0 ? pattern : nil }
                    ))
                }
            }
            Menu("Guides") {
                Toggle("Rule of Thirds", isOn: $model.prefs.guides.thirds)
                Toggle("Center Cross", isOn: $model.prefs.guides.center)
                Toggle("Safe Areas (90 % / 80 %)", isOn: $model.prefs.guides.safeAreas)
                Divider()
                Toggle("No Aspect Mask", isOn: Binding(get: { model.prefs.guides.mask == nil }, set: { _ in model.prefs.guides.mask = nil }))
                ForEach(GuideSettings.Mask.allCases, id: \.self) { mask in
                    Toggle("Mask \(mask.rawValue)", isOn: Binding(
                        get: { model.prefs.guides.mask == mask },
                        set: { model.prefs.guides.mask = $0 ? mask : nil }
                    ))
                }
            }
            Divider()
            Menu("Video Input") {
                ForEach(model.catalog.videoDevices) { device in
                    Toggle(device.name, isOn: Binding(
                        get: { model.selectedVideoID == device.id },
                        set: { _ in model.selectVideo(device.id) }
                    ))
                }
            }
            Button("Reconnect") { model.retry() }
                .keyboardShortcut("r", modifiers: [.command, .option])
        }
    }
}

/// Pro actions: locked items open the upgrade sheet; unlocked-but-unbuilt items are disabled.
struct ProCommands: Commands {
    let store: EntitlementStore

    var body: some Commands {
        CommandMenu("Pro") {
            item(.recording, "Start Recording", key: "r", modifiers: .command)
            item(.replayBuffer, "Save Instant Replay", key: "r", modifiers: [.command, .shift])
            item(.streaming, "Go Live…", key: "l", modifiers: [.command, .shift])
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
            store.gate(feature)
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
