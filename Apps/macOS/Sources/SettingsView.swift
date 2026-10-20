import CaptureCore
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section("Input") {
                Picker("Video", selection: Binding(
                    get: { model.selectedVideoID ?? "" },
                    set: { if !$0.isEmpty { model.selectVideo($0) } }
                )) {
                    if model.selectedVideoID == nil { Text("None").tag("") }
                    ForEach(model.catalog.videoDevices) { Text($0.name).tag($0.id) }
                }
                Toggle("Show built-in and Continuity cameras", isOn: $model.prefs.includeNonExternalCameras)
                Picker("Audio", selection: $model.preset.audio) {
                    Text("Automatic").tag(DevicePreset.AudioSelection.automatic)
                    Text("None").tag(DevicePreset.AudioSelection.none)
                    Divider()
                    ForEach(model.catalog.audioDevices) { Text($0.name).tag(DevicePreset.AudioSelection.device($0.id)) }
                }
                Picker("Format", selection: $model.preset.format) {
                    Text("Automatic").tag(FormatPreference.auto)
                    Divider()
                    ForEach(model.formatOptions, id: \.self) { Text(label(for: $0)).tag($0) }
                }
            }
            .disabled(model.selectedVideoID == nil)

            Section("Picture") {
                Picker("Scaling", selection: $model.preset.scale) {
                    ForEach(ScaleMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                Picker("Filtering", selection: $model.preset.filter) {
                    Text("Smooth").tag(FilterMode.linear)
                    Text("Sharp pixels").tag(FilterMode.nearest)
                }
                Picker("Color range", selection: $model.preset.colorRange) {
                    Text("Automatic").tag(ColorRange.auto)
                    Text("Limited (16–235)").tag(ColorRange.limited)
                    Text("Full (0–255)").tag(ColorRange.full)
                }
            }
            .disabled(model.selectedVideoID == nil)

            Section("Sound") {
                Slider(value: $model.preset.volume, in: 0...1) { Text("Volume") }
                Toggle("Mute", isOn: $model.preset.muted)
            }
            .disabled(model.selectedVideoID == nil)

            Section("Window") {
                Toggle("Lock aspect ratio", isOn: $model.prefs.lockAspect)
                Toggle("Float on top", isOn: $model.prefs.floatOnTop)
                Toggle("Show on all Spaces", isOn: $model.prefs.allSpaces)
                Toggle("Clean mode (hide title bar)", isOn: $model.prefs.cleanMode)
                Toggle("Show stats", isOn: $model.prefs.showStats)
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
    }

    private func label(for preference: FormatPreference) -> String {
        guard case let .pinned(width, height, rate) = preference else { return "Automatic" }
        guard let rate else { return "\(width)×\(height)" }
        return "\(width)×\(height) @ \(rate.formatted(.number.precision(.fractionLength(0...2)))) fps"
    }
}

extension ScaleMode {
    var title: String {
        switch self {
        case .fit: "Fit"
        case .fill: "Fill (crop)"
        case .stretch: "Stretch"
        case .integer: "Integer (pixel-perfect)"
        }
    }
}
