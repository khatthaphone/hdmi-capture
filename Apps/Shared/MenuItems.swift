import CaptureCore
import SwiftUI

// Menu contents shared by the menu bar (macOS / iPadOS) and the control bar.

/// Picture Look presets; shared by the Video menu and the control bar.
struct PictureLookItems: View {
    let viewer: ViewerModel

    var body: some View {
        ForEach(ImageAdjustments.Preset.allCases, id: \.self) { preset in
            Toggle(preset.title, isOn: Binding(
                get: { viewer.preset.adjustments == preset.values },
                set: { _ in viewer.preset.adjustments = preset.values }
            ))
        }
    }
}

/// Frame guides; shared by the Video menu and the control bar.
struct GuideItems: View {
    @Bindable var app: AppModel

    var body: some View {
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
}

/// The Save Recent spans; shared by the Pro menu and the control bar.
struct SaveRecentItems: View {
    let app: AppModel
    let store: EntitlementStore
    let viewer: ViewerModel?
    /// Menu-bar items carry ⇧⌘R / ⌥⇧⌘R; the control bar's copy must not register them twice.
    var shortcuts = false

    private static let spans: [(title: String, seconds: Double)] = [
        ("Last 15 Seconds", 15), ("Last 30 Seconds", 30), ("Last 1 Minute", 60), ("Last 5 Minutes", 300),
        ("Last 15 Minutes", 900), ("Last 30 Minutes", 1800), ("Last 60 Minutes", 3600),
    ]

    var body: some View {
        let enabled = app.prefs.recentEnabled && store.isUnlocked(.replayBuffer)
        let buffered = viewer?.session?.recent.bufferedSeconds ?? 0
        ForEach(Self.spans.filter { $0.seconds <= Double(app.prefs.recentMinutes * 60) }, id: \.seconds) { span in
            let button = Button(span.title) { save(span.seconds) }
                // Allow a few seconds of slack: segments close every 10 s.
                .disabled(!enabled || buffered + 10 < span.seconds)
            if shortcuts, span.seconds == 30 {
                button.keyboardShortcut("r", modifiers: [.command, .shift])
            } else {
                button
            }
        }
        Divider()
        let everything = Button("Everything (\(DeviceSession.describe(buffered)))") { save(nil) }
            .disabled(!enabled || buffered < 1)
        if shortcuts {
            everything.keyboardShortcut("r", modifiers: [.command, .shift, .option])
        } else {
            everything
        }
    }

    private func save(_ seconds: Double?) {
        guard store.gate(.replayBuffer) else { return }
        viewer?.session?.saveRecent(seconds: seconds)
    }
}
