import CaptureCore
import SwiftUI
import UniformTypeIdentifiers

enum SettingsPage {
    case input, picture, sound, window
}

/// One Settings tab. Card pages act on the card in the key window (or the main window).
struct SettingsView: View {
    @Environment(AppModel.self) private var app
    let page: SettingsPage

    var body: some View {
        Group {
            if page == .window {
                WindowSettingsPage()
            } else if let viewer = app.currentViewer, viewer.session != nil || page == .input {
                CardSettingsPage(page: page).environment(viewer)
            } else {
                ContentUnavailableView {
                    Label("No active capture card", systemImage: "cable.connector.slash")
                } description: {
                    Text("Connect a card, or bring its HDMI Captain window to the front.")
                }
            }
        }
        // One size for every tab, so switching tabs doesn't make the window jump.
        .frame(width: 560, height: 620)
    }
}

private struct CardSettingsPage: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @State private var outputDevices: [AudioOutputDevice] = []
    let page: SettingsPage

    var body: some View {
        Form {
            switch page {
            case .input: input
            case .picture: picture
            case .sound: sound
            case .window: EmptyView()
            }
        }
        .formStyle(.grouped)
        .onAppear { outputDevices = app.outputDevices() }
    }

    @ViewBuilder private var input: some View {
        @Bindable var viewer = viewer
        @Bindable var app = app
        Section {
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
            if viewer.session == nil {
                Label("Connect or select a capture card to change its audio and format.", systemImage: "cable.connector.slash")
                    .foregroundStyle(.secondary)
            }
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
            .disabled(viewer.session == nil)
        }
        Section {
            Toggle("Show built-in and Continuity cameras", isOn: $app.prefs.includeNonExternalCameras)
        } footer: {
            Text("Handy for trying the app without a capture card.").font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private var picture: some View {
        @Bindable var viewer = viewer
        Section {
            Picker("Scale", selection: $viewer.preset.scale) {
                ForEach(ScaleMode.allCases, id: \.self) { Text($0.title).tag($0) }
            }
            Picker("Image filtering", selection: $viewer.preset.filter) {
                Text("Smooth").tag(FilterMode.linear)
                Text("Pixel-sharp").tag(FilterMode.nearest)
            }
            Picker("Color range", selection: $viewer.preset.colorRange) {
                Text("Automatic").tag(ColorRange.auto)
                Text("Limited (16–235)").tag(ColorRange.limited)
                Text("Full (0–255)").tag(ColorRange.full)
            }
        }
        Section("Look") {
            Picker("Preset", selection: Binding(
                get: { viewer.preset.adjustments.preset },
                set: { if let preset = $0 { viewer.preset.adjustments = preset.values } }
            )) {
                ForEach(ImageAdjustments.Preset.allCases, id: \.self) { Text($0.title).tag(Optional($0)) }
                if viewer.preset.adjustments.preset == nil { Text("Custom").tag(ImageAdjustments.Preset?.none) }
            }
            LabeledContent("Brightness") {
                Slider(value: $viewer.preset.adjustments.brightness, in: -0.5...0.5)
                Text(String(format: "%+.2f", viewer.preset.adjustments.brightness)).monospacedDigit().frame(width: 52, alignment: .trailing)
            }
            LabeledContent("Contrast") {
                Slider(value: $viewer.preset.adjustments.contrast, in: 0.5...1.5)
                Text(String(format: "%.2f×", viewer.preset.adjustments.contrast)).monospacedDigit().frame(width: 52, alignment: .trailing)
            }
            LabeledContent("Saturation") {
                Slider(value: $viewer.preset.adjustments.saturation, in: 0...2)
                Text(String(format: "%.0f%%", viewer.preset.adjustments.saturation * 100)).monospacedDigit().frame(width: 52, alignment: .trailing)
            }
            LabeledContent("Gamma") {
                Slider(value: $viewer.preset.adjustments.gamma, in: 0.5...2)
                Text(String(format: "%.2f", viewer.preset.adjustments.gamma)).monospacedDigit().frame(width: 52, alignment: .trailing)
            }
            LabeledContent("Sharpness") {
                Slider(value: $viewer.preset.adjustments.sharpness, in: 0...1)
                Text(String(format: "%.0f%%", viewer.preset.adjustments.sharpness * 100)).monospacedDigit().frame(width: 52, alignment: .trailing)
            }
            HStack {
                Spacer()
                Button("Reset Look") { viewer.preset.adjustments = ImageAdjustments() }
                    .disabled(viewer.preset.adjustments == ImageAdjustments())
            }
        }
        Section("LUT") {
            LabeledContent("File") {
                HStack {
                    Text(viewer.preset.lutName ?? "None")
                        .foregroundStyle(viewer.session?.lutError == nil ? .primary : Color.red)
                        .help(viewer.session?.lutError ?? "")
                    Button("Choose…") { chooseLUT() }
                    if viewer.preset.lutName != nil {
                        Button("Remove") { viewer.session?.setLUT(url: nil) }
                    }
                }
            }
            if viewer.preset.lutName != nil {
                Slider(value: $viewer.preset.lutIntensity, in: 0...1) { Text("Intensity") }
            }
        }
    }

    @ViewBuilder private var sound: some View {
        @Bindable var viewer = viewer
        @Bindable var app = app
        Section {
            Picker("Audio output", selection: $viewer.preset.outputDeviceUID) {
                Text("System default").tag(String?.none)
                Divider()
                ForEach(outputDevices) { Text($0.name).tag(Optional($0.id)) }
            }
            Slider(value: $viewer.preset.volume, in: 0...1) { Text("Volume") }
            Toggle("Mute", isOn: $viewer.preset.muted)
            LabeledContent("Level") { LevelMeter() }
        }
        Section {
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
            Toggle("Play sound from the frontmost window", isOn: $app.prefs.audioFollowsFocus)
        }
    }

    private func chooseLUT() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "cube") ?? .data]
        panel.allowsMultipleSelection = false
        panel.message = "Choose a .cube LUT (for example a Sony S-Log3 to Rec.709 conversion)."
        if panel.runModal() == .OK, let url = panel.url {
            viewer.session?.setLUT(url: url)
        }
    }

    private func label(for preference: FormatPreference) -> String {
        guard case let .pinned(width, height, rate) = preference else { return "Automatic" }
        guard let rate else { return "\(width)×\(height)" }
        return "\(width)×\(height) @ \(rate.formatted(.number.precision(.fractionLength(0...2)))) fps"
    }
}

/// App-wide window and overlay options (no card needed).
private struct WindowSettingsPage: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        @Bindable var app = app
        Form {
            Section("Viewer") {
                Toggle("Lock aspect ratio", isOn: $app.prefs.lockAspect)
                Toggle("Keep window on top", isOn: $app.prefs.floatOnTop)
                Toggle("Show on all Spaces", isOn: $app.prefs.allSpaces)
                Toggle("Hide window buttons (clean mode)", isOn: $app.prefs.cleanMode)
                Toggle("Show controls near the bottom edge", isOn: $app.prefs.showControlsOnHover)
                Toggle("Show diagnostics", isOn: $app.prefs.showStats)
            }
            Section("Menu Bar") {
                Toggle("Show menu bar item", isOn: $app.prefs.showMenuBarItem)
                Toggle("Menu bar only (hide Dock icon)", isOn: $app.prefs.menuBarOnly)
                    .disabled(!app.prefs.showMenuBarItem)
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
