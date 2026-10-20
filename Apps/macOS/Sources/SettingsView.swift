import CaptureCore
import SwiftUI

/// Settings act on the card in the key window (or the main window).
struct SettingsView: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        if let viewer = app.currentViewer {
            CardSettingsView().environment(viewer)
        } else {
            Text("Open the HDMI Captain window to change settings.")
                .foregroundStyle(.secondary)
                .padding(40)
        }
    }
}

private struct CardSettingsView: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @State private var outputDevices: [AudioOutputDevice] = []

    var body: some View {
        @Bindable var viewer = viewer
        @Bindable var app = app
        Form {
            Section("Input") {
                if viewer.source == .automatic {
                    Picker("Video", selection: Binding(
                        get: { app.mainDeviceID ?? "" },
                        set: { if !$0.isEmpty { app.selectVideo($0) } }
                    )) {
                        if app.mainDeviceID == nil { Text("None").tag("") }
                        ForEach(app.catalog.videoDevices) { Text($0.name).tag($0.id) }
                    }
                } else {
                    LabeledContent("Video", value: viewer.session?.deviceName ?? "Not connected")
                }
                Toggle("Show built-in and Continuity cameras", isOn: $app.prefs.includeNonExternalCameras)
                Picker("Audio", selection: $viewer.preset.audio) {
                    Text("Automatic").tag(DevicePreset.AudioSelection.automatic)
                    Text("None").tag(DevicePreset.AudioSelection.none)
                    Divider()
                    ForEach(app.catalog.audioDevices) { Text($0.name).tag(DevicePreset.AudioSelection.device($0.id)) }
                }
                Picker("Format", selection: $viewer.preset.format) {
                    Text("Automatic").tag(FormatPreference.auto)
                    Divider()
                    ForEach(viewer.formatOptions, id: \.self) { Text(label(for: $0)).tag($0) }
                }
            }
            .disabled(viewer.session == nil)

            Section("Picture") {
                Picker("Scaling", selection: $viewer.preset.scale) {
                    ForEach(ScaleMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                Picker("Filtering", selection: $viewer.preset.filter) {
                    Text("Smooth").tag(FilterMode.linear)
                    Text("Sharp pixels").tag(FilterMode.nearest)
                }
                Picker("Color range", selection: $viewer.preset.colorRange) {
                    Text("Automatic").tag(ColorRange.auto)
                    Text("Limited (16–235)").tag(ColorRange.limited)
                    Text("Full (0–255)").tag(ColorRange.full)
                }
                Picker("Look", selection: Binding(
                    get: { viewer.preset.adjustments.preset },
                    set: { if let preset = $0 { viewer.preset.adjustments = preset.values } }
                )) {
                    ForEach(ImageAdjustments.Preset.allCases, id: \.self) { Text($0.title).tag(Optional($0)) }
                    if viewer.preset.adjustments.preset == nil { Text("Custom").tag(ImageAdjustments.Preset?.none) }
                }
                Slider(value: $viewer.preset.adjustments.brightness, in: -0.5...0.5) { Text("Brightness") }
                Slider(value: $viewer.preset.adjustments.contrast, in: 0.5...1.5) { Text("Contrast") }
                Slider(value: $viewer.preset.adjustments.saturation, in: 0...2) { Text("Saturation") }
                Slider(value: $viewer.preset.adjustments.gamma, in: 0.5...2) { Text("Gamma") }
                Slider(value: $viewer.preset.adjustments.sharpness, in: 0...1) { Text("Sharpness") }
                Button("Reset Picture") { viewer.preset.adjustments = ImageAdjustments() }
                    .disabled(viewer.preset.adjustments == ImageAdjustments())
            }
            .disabled(viewer.session == nil)

            Section("Sound") {
                Picker("Output", selection: $viewer.preset.outputDeviceUID) {
                    Text("System default").tag(String?.none)
                    Divider()
                    ForEach(outputDevices) { Text($0.name).tag(Optional($0.id)) }
                }
                Slider(value: $viewer.preset.volume, in: 0...1) { Text("Volume") }
                Toggle("Mute", isOn: $viewer.preset.muted)
                LabeledContent("Level") { LevelMeter() }
                Slider(
                    value: Binding(get: { Double(viewer.preset.audioDelayMs) }, set: { viewer.preset.audioDelayMs = Int($0) }),
                    in: 0...500, step: 10
                ) {
                    Text("Audio delay \(viewer.preset.audioDelayMs) ms").monospacedDigit()
                }
                .help("Delay the sound to line it up with the picture.")
                Picker("Channels", selection: $viewer.preset.channelMode) {
                    ForEach(ChannelMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
            }
            .disabled(viewer.session == nil)

            Section("Window") {
                Toggle("Lock aspect ratio", isOn: $app.prefs.lockAspect)
                Toggle("Float on top", isOn: $app.prefs.floatOnTop)
                Toggle("Show on all Spaces", isOn: $app.prefs.allSpaces)
                Toggle("Clean mode (hide title bar)", isOn: $app.prefs.cleanMode)
                Toggle("Show stats", isOn: $app.prefs.showStats)
                Toggle("Show menu bar item", isOn: $app.prefs.showMenuBarItem)
                Toggle("Menu bar only (hide Dock icon)", isOn: $app.prefs.menuBarOnly)
                    .disabled(!app.prefs.showMenuBarItem)
                Toggle("Sound follows the focused window", isOn: $app.prefs.audioFollowsFocus)
            }

            Section("Guides") {
                Toggle("Rule of thirds", isOn: $app.prefs.guides.thirds)
                Toggle("Center cross", isOn: $app.prefs.guides.center)
                Toggle("Safe areas", isOn: $app.prefs.guides.safeAreas)
                Picker("Aspect mask", selection: $app.prefs.guides.mask) {
                    Text("None").tag(GuideSettings.Mask?.none)
                    ForEach(GuideSettings.Mask.allCases, id: \.self) { Text($0.rawValue).tag(Optional($0)) }
                }
                Slider(value: $app.prefs.guides.opacity, in: 0.2...1) { Text("Opacity") }
            }
        }
        .formStyle(.grouped)
        .onAppear { outputDevices = app.outputDevices() }
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
    @Environment(ViewerModel.self) private var viewer

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.05)) { _ in
            let levels = viewer.audioLevels()
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
