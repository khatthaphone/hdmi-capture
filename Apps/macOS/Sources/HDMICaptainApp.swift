import AppKit
import CaptureCore
import SwiftUI

@main
struct HDMICaptainApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = AppModel()

    var body: some Scene {
        Window("HDMI Captain", id: "main") {
            ContentView().environment(model)
        }
        .defaultSize(width: 1280, height: 720)
        .commands { ViewerCommands(model: model) }

        Settings {
            SettingsView().environment(model)
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
            Menu("Video Input") {
                ForEach(model.catalog.videoDevices) { device in
                    Toggle(device.name, isOn: Binding(
                        get: { model.selectedVideoID == device.id },
                        set: { _ in model.selectVideo(device.id) }
                    ))
                }
            }
            Button("Reconnect") { model.retry() }
                .keyboardShortcut("r")
        }
    }
}
