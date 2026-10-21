import CaptureCore
import SwiftUI
import UniformTypeIdentifiers

/// iPad Settings, shown as a sheet from the control bar's ⋯ menu or ⌘,.
/// Same preferences as the Mac's Settings window, minus window and menu-bar options.
struct SettingsSheet: View {
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Group {
                if let viewer = app.currentViewer {
                    SettingsForm().environment(viewer)
                } else {
                    ContentUnavailableView("No capture card", systemImage: Symbol.input,
                                           description: Text("Connect an HDMI capture card to change its settings."))
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }
}

private struct SettingsForm: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @State private var choosingLUT = false

    var body: some View {
        @Bindable var viewer = viewer
        @Bindable var app = app
        Form {
            Section("Input") {
                Picker("Capture card", selection: Binding(
                    get: { app.mainDeviceID ?? "" },
                    set: { if !$0.isEmpty { app.selectVideo($0) } }
                )) {
                    if app.mainDeviceID == nil { Text("None").tag("") }
                    ForEach(app.catalog.videoDevices) { Text($0.name).tag($0.id) }
                }
                Picker("Format", selection: $viewer.preset.format) {
                    Text("Automatic").tag(FormatPreference.auto)
                    ForEach(viewer.formatOptions, id: \.self) { Text(label(for: $0)).tag($0) }
                }
                .disabled(noCard)
                if noCard {
                    Label("Connect a capture card to change its picture and sound.", systemImage: "cable.connector.slash")
                        .foregroundStyle(.secondary)
                }
            }

            Section("Picture") {
                Picker("Scaling", selection: $viewer.preset.scale) {
                    ForEach(ScaleMode.allCases, id: \.self) { Label($0.title, systemImage: $0.symbol).tag($0) }
                }
                Toggle("Sharp pixels", isOn: Binding(
                    get: { viewer.preset.filter == .nearest },
                    set: { viewer.preset.filter = $0 ? .nearest : .linear }
                ))
                Picker("Color range", selection: $viewer.preset.colorRange) {
                    Text("Automatic").tag(ColorRange.auto)
                    Text("Limited (16–235)").tag(ColorRange.limited)
                    Text("Full (0–255)").tag(ColorRange.full)
                }
            }
            .disabled(noCard)

            Section("Look") {
                Picker("Preset", selection: Binding(
                    get: { viewer.preset.adjustments.preset },
                    set: { if let preset = $0 { viewer.preset.adjustments = preset.values } }
                )) {
                    ForEach(ImageAdjustments.Preset.allCases, id: \.self) { Text($0.title).tag(Optional($0)) }
                    if viewer.preset.adjustments.preset == nil { Text("Custom").tag(ImageAdjustments.Preset?.none) }
                }
                LabeledSlider("Brightness", value: $viewer.preset.adjustments.brightness, in: -0.5...0.5)
                LabeledSlider("Contrast", value: $viewer.preset.adjustments.contrast, in: 0.5...1.5)
                LabeledSlider("Saturation", value: $viewer.preset.adjustments.saturation, in: 0...2)
                LabeledSlider("Gamma", value: $viewer.preset.adjustments.gamma, in: 0.5...2)
                LabeledSlider("Sharpness", value: $viewer.preset.adjustments.sharpness, in: 0...1)
                Button("Reset Look") { viewer.preset.adjustments = ImageAdjustments() }
                    .disabled(viewer.preset.adjustments == ImageAdjustments())
            }
            .disabled(noCard)

            Section("LUT") {
                LabeledContent("File", value: viewer.preset.lutName ?? "None")
                if let error = viewer.session?.lutError {
                    Text(error).font(.footnote).foregroundStyle(.red)
                }
                Button("Choose .cube File…") { choosingLUT = true }
                if viewer.preset.lutName != nil {
                    LabeledSlider("Intensity", value: $viewer.preset.lutIntensity, in: 0...1)
                    Button("Remove LUT", role: .destructive) { viewer.session?.setLUT(url: nil) }
                }
            }
            .disabled(noCard)

            Section {
                Toggle("Mute", isOn: $viewer.preset.muted)
                LabeledSlider("Volume", value: $viewer.preset.volume, in: 0...1)
                LabeledSlider("Audio delay", value: Binding(
                    get: { Double(viewer.preset.audioDelayMs) },
                    set: { viewer.preset.audioDelayMs = Int($0) }
                ), in: 0...500, step: 10, valueText: "\(viewer.preset.audioDelayMs) ms")
                Picker("Channels", selection: $viewer.preset.channelMode) {
                    ForEach(ChannelMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
            } header: {
                Text("Sound")
            } footer: {
                Text("HDMI sound plays only when the capture card is the iPad's audio input, never the built-in microphone. Use the delay to line sound up with the picture.")
            }
            .disabled(noCard)

            Section("Display") {
                Toggle("Show controls on tap", isOn: $app.prefs.showControlsOnHover)
                Toggle("Show stats", isOn: $app.prefs.showStats)
                Toggle("Show recording timer", isOn: $app.prefs.showRecordingIndicator)
            }

            Section("Guides") {
                GuideItems(app: app)
                LabeledSlider("Opacity", value: $app.prefs.guides.opacity, in: 0.2...1)
            }

            ProSettingsSections()
        }
        .fileImporter(isPresented: $choosingLUT, allowedContentTypes: [UTType(filenameExtension: "cube") ?? .data]) { result in
            guard case let .success(url) = result else { return }
            // Files outside the app's container need security-scoped access while we read them.
            let accessing = url.startAccessingSecurityScopedResource()
            defer { if accessing { url.stopAccessingSecurityScopedResource() } }
            viewer.session?.setLUT(url: url)
        }
    }

    /// Card settings need a card; app-wide ones (display, guides, Pro) don't.
    private var noCard: Bool { viewer.session == nil }

    private func label(for preference: FormatPreference) -> String {
        guard case let .pinned(width, height, rate) = preference else { return "Automatic" }
        guard let rate else { return "\(width)×\(height)" }
        return "\(width)×\(height) @ \(rate.formatted(.number.precision(.fractionLength(0...2)))) fps"
    }
}

/// A slider with its name and value on one line above it (iOS sliders show no label).
private struct LabeledSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    var step: Double?
    var valueText: String?

    init(_ title: String, value: Binding<Double>, in range: ClosedRange<Double>, step: Double? = nil, valueText: String? = nil) {
        self.title = title
        _value = value
        self.range = range
        self.step = step
        self.valueText = valueText
    }

    init(_ title: String, value: Binding<Float>, in range: ClosedRange<Double>) {
        self.init(title, value: Binding(get: { Double(value.wrappedValue) }, set: { value.wrappedValue = Float($0) }), in: range)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title)
                Spacer()
                Text(valueText ?? value.formatted(.number.precision(.fractionLength(2))))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            if let step {
                Slider(value: $value, in: range, step: step)
            } else {
                Slider(value: $value, in: range)
            }
        }
    }
}
