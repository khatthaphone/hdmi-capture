import CaptureCore
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var outputDevices: [AudioOutputDevice] = []

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
                Picker("Look", selection: Binding(
                    get: { model.preset.adjustments.preset },
                    set: { if let preset = $0 { model.preset.adjustments = preset.values } }
                )) {
                    ForEach(ImageAdjustments.Preset.allCases, id: \.self) { Text($0.title).tag(Optional($0)) }
                    if model.preset.adjustments.preset == nil { Text("Custom").tag(ImageAdjustments.Preset?.none) }
                }
                Slider(value: $model.preset.adjustments.brightness, in: -0.5...0.5) { Text("Brightness") }
                Slider(value: $model.preset.adjustments.contrast, in: 0.5...1.5) { Text("Contrast") }
                Slider(value: $model.preset.adjustments.saturation, in: 0...2) { Text("Saturation") }
                Slider(value: $model.preset.adjustments.gamma, in: 0.5...2) { Text("Gamma") }
                Slider(value: $model.preset.adjustments.sharpness, in: 0...1) { Text("Sharpness") }
                Button("Reset Picture") { model.preset.adjustments = ImageAdjustments() }
                    .disabled(model.preset.adjustments == ImageAdjustments())
            }
            .disabled(model.selectedVideoID == nil)

            Section("Sound") {
                Picker("Output", selection: $model.preset.outputDeviceUID) {
                    Text("System default").tag(String?.none)
                    Divider()
                    ForEach(outputDevices) { Text($0.name).tag(Optional($0.id)) }
                }
                Slider(value: $model.preset.volume, in: 0...1) { Text("Volume") }
                Toggle("Mute", isOn: $model.preset.muted)
                LabeledContent("Level") { LevelMeter() }
                Slider(
                    value: Binding(get: { Double(model.preset.audioDelayMs) }, set: { model.preset.audioDelayMs = Int($0) }),
                    in: 0...500, step: 10
                ) {
                    Text("Audio delay \(model.preset.audioDelayMs) ms").monospacedDigit()
                }
                .help("Delay the sound to line it up with the picture.")
                Picker("Channels", selection: $model.preset.channelMode) {
                    ForEach(ChannelMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
            }
            .disabled(model.selectedVideoID == nil)

            Section("Window") {
                Toggle("Lock aspect ratio", isOn: $model.prefs.lockAspect)
                Toggle("Float on top", isOn: $model.prefs.floatOnTop)
                Toggle("Show on all Spaces", isOn: $model.prefs.allSpaces)
                Toggle("Clean mode (hide title bar)", isOn: $model.prefs.cleanMode)
                Toggle("Show stats", isOn: $model.prefs.showStats)
                Toggle("Show menu bar item", isOn: $model.prefs.showMenuBarItem)
                Toggle("Menu bar only (hide Dock icon)", isOn: $model.prefs.menuBarOnly)
                    .disabled(!model.prefs.showMenuBarItem)
            }

            Section("Guides") {
                Toggle("Rule of thirds", isOn: $model.prefs.guides.thirds)
                Toggle("Center cross", isOn: $model.prefs.guides.center)
                Toggle("Safe areas", isOn: $model.prefs.guides.safeAreas)
                Picker("Aspect mask", selection: $model.prefs.guides.mask) {
                    Text("None").tag(GuideSettings.Mask?.none)
                    ForEach(GuideSettings.Mask.allCases, id: \.self) { Text($0.rawValue).tag(Optional($0)) }
                }
                Slider(value: $model.prefs.guides.opacity, in: 0.2...1) { Text("Opacity") }
            }
        }
        .formStyle(.grouped)
        .onAppear { outputDevices = model.outputDevices() }
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

extension ChannelMode {
    var title: String {
        switch self {
        case .auto: "Automatic"
        case .normal: "Stereo"
        case .swap: "Swap left and right"
        case .leftToBoth: "Left to both"
        case .rightToBoth: "Right to both"
        case .mono: "Mono mix"
        }
    }
}

/// Two horizontal peak/RMS bars, polled at 20 Hz.
struct LevelMeter: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.05)) { _ in
            let levels = model.audioLevels()
            VStack(spacing: 3) {
                bar(rms: levels.rms.x, peak: levels.peak.x)
                bar(rms: levels.rms.y, peak: levels.peak.y)
            }
            .frame(width: 180)
            .help(String(format: "Buffered %.0f ms", levels.bufferedMs))
        }
    }

    private func bar(rms: Float, peak: Float) -> some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(.quaternary)
                Capsule().fill(.green.gradient).frame(width: geo.size.width * CGFloat(Self.scale(rms)))
                Rectangle().fill(peak > 0.98 ? .red : .primary).frame(width: 2)
                    .offset(x: geo.size.width * CGFloat(Self.scale(peak)) - 1)
            }
        }
        .frame(height: 5)
    }

    /// Maps linear amplitude to a 60 dB meter.
    static func scale(_ value: Float) -> Float {
        guard value > 0 else { return 0 }
        return max(0, min(1, (20 * log10(value) + 60) / 60))
    }
}
