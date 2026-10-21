import CaptureCore
import SwiftUI

/// Floating control bar shown when the cursor nears the bottom edge of a viewer window.
///
/// It is a window-server overlay only: recordings, Save Recent, streams and screenshots read the
/// capture engine's sample buffers (or `MetalRenderer.currentFrame`), so the bar never ends up in them.
struct ControlBar: View {
    @Environment(ViewerModel.self) private var viewer

    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        // Widest layout that fits: labels → icons only → essentials.
        ViewThatFits(in: .horizontal) {
            bar(.full)
            bar(.compact)
            bar(.minimal)
        }
        .padding(.horizontal, 12)
        .onHover { viewer.controlsHovered = $0 }
        .environment(\.colorScheme, .dark)
    }

    private func bar(_ layout: ControlBarLayout) -> some View {
        HStack(spacing: 4) {
            InputChip(layout: layout)
            if layout != .minimal {
                BarDivider()
                SoundControl(layout: layout)
                BarDivider()
                ScreenshotButton(layout: layout)
            }
            RecordButton(layout: layout)
            if layout != .minimal {
                SaveRecentButton(layout: layout)
                #if os(macOS)
                if ProFeature.shown.contains(.streaming) {
                    BarDivider()
                    GoLiveButton(layout: layout)
                }
                #endif
            }
            BarDivider()
            OptionsMenu(layout: layout)
            #if os(macOS)
            if layout != .minimal {
                FullScreenButton()
            }
            #endif
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 5)
        .fixedSize()
        .background {
            // See-through, but dark enough that white labels stay readable over bright video;
            // Increase Contrast makes it solid.
            Capsule()
                .fill(.black.opacity(contrast == .increased ? 0.88 : 0.4))
                .background(.ultraThinMaterial.opacity(0.6), in: Capsule())
        }
        .overlay(Capsule().strokeBorder(.white.opacity(0.14), lineWidth: 1))
        .shadow(color: .black.opacity(0.35), radius: 12, y: 4)
        // Clicks on the bar's padding must not drag the window.
        .contentShape(Capsule())
    }
}

enum ControlBarLayout {
    /// Icons with labels.
    case full
    /// Icons only.
    case compact
    /// Input, Record and Options; everything else is in Options.
    case minimal

    var showsLabels: Bool { self == .full }
}

// MARK: - Controls

private struct InputChip: View {
    let layout: ControlBarLayout
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Menu {
            if viewer.source == .automatic {
                Section("Video Input") {
                    ForEach(app.catalog.videoDevices) { device in
                        Toggle(device.name, isOn: Binding(
                            get: { app.mainDeviceID == device.id },
                            set: { _ in app.selectVideo(device.id) }
                        ))
                    }
                }
            }
            #if os(macOS)
            if !app.catalog.videoDevices.isEmpty {
                Menu {
                    ForEach(app.catalog.videoDevices) { device in
                        Button(device.name) { openWindow(id: "card", value: device.id) }
                    }
                } label: {
                    Label("New Window for", systemImage: Symbol.newWindow)
                }
            }
            #endif
            if !app.profiles.isEmpty {
                Section("Profiles") {
                    ForEach(app.profiles) { profile in
                        Toggle(isOn: Binding(
                            get: { viewer.preset.profileID == profile.id },
                            set: { _ in viewer.session?.applyProfile(profile) }
                        )) {
                            Label(profile.name, systemImage: profile.symbol)
                        }
                        .disabled(viewer.session == nil)
                    }
                }
            }
            Divider()
            Button { app.retry() } label: { Label("Reconnect", systemImage: Symbol.reconnect) }
        } label: {
            BarLabel(symbol: viewer.testPattern != nil ? Symbol.testPattern : Symbol.input, title: title,
                     showsTitle: layout.showsLabels, chevron: true)
        }
        .barMenuStyle()
        .help("Input and profiles")
        .accessibilityLabel("Input: \(title)")
    }

    private var title: String {
        if viewer.testPattern != nil { return "Test Pattern" }
        let name = viewer.session?.deviceName ?? "No Input"
        // Long USB names ("UGREEN HDMI Capture Device") crowd the bar.
        return name.count > 22 ? String(name.prefix(20)) + "…" : name
    }
}

private struct SoundControl: View {
    let layout: ControlBarLayout
    @Environment(ViewerModel.self) private var viewer

    var body: some View {
        @Bindable var viewer = viewer
        HStack(spacing: 2) {
            Button {
                viewer.preset.muted.toggle()
            } label: {
                BarLabel(symbol: viewer.preset.muted ? Symbol.mute : Symbol.sound, title: viewer.preset.muted ? "Unmute" : "Mute",
                         showsTitle: false)
            }
            .buttonStyle(BarButtonStyle())
            .help(viewer.preset.muted ? "Unmute (⇧⌘M)" : "Mute (⇧⌘M)")
            if layout == .full {
                VStack(spacing: 3) {
                    Slider(value: $viewer.preset.volume, in: 0...1)
                        .accessibilityLabel("Volume")
                        .controlSize(.mini)
                        .frame(width: 72)
                    BarLevelMeter()
                        .frame(width: 66, height: 2)
                }
                .disabled(viewer.preset.muted)
                .opacity(viewer.preset.muted ? 0.4 : 1)
                .padding(.trailing, 4)
            }
        }
        .disabled(viewer.session == nil)
    }
}

/// Thin live level line under the volume slider.
private struct BarLevelMeter: View {
    @Environment(ViewerModel.self) private var viewer

    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 20)) { _ in
            let peak = viewer.preset.muted ? 0 : CGFloat(max(viewer.audioLevels().peak.x, viewer.audioLevels().peak.y))
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(.white.opacity(0.15))
                    Capsule()
                        .fill(peak > 0.9 ? Color.red : peak > 0.6 ? .yellow : .green)
                        .frame(width: geometry.size.width * min(1, peak))
                }
            }
        }
    }
}

private struct ScreenshotButton: View {
    let layout: ControlBarLayout
    @Environment(ViewerModel.self) private var viewer

    var body: some View {
        Button { viewer.saveScreenshot() } label: {
            BarLabel(symbol: Symbol.screenshot, title: "Screenshot", showsTitle: layout.showsLabels)
        }
        .buttonStyle(BarButtonStyle())
        .help("Save Screenshot (⇧⌘S)")
    }
}

private struct SaveRecentButton: View {
    let layout: ControlBarLayout
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @Environment(EntitlementStore.self) private var store

    var body: some View {
        Menu {
            Toggle(isOn: Binding(
                get: { app.prefs.recentEnabled && store.isUnlocked(.replayBuffer) },
                set: { on in if !on || store.gate(.replayBuffer) { app.prefs.recentEnabled = on } }
            )) {
                Label("Keep Recent Footage (\(app.prefs.recentMinutes) min)",
                      systemImage: store.isUnlocked(.replayBuffer) ? Symbol.keepRecent : Symbol.locked)
            }
            Divider()
            SaveRecentItems(app: app, store: store, viewer: viewer)
        } label: {
            BarLabel(symbol: Symbol.saveRecent, title: "Save Recent", showsTitle: layout.showsLabels, chevron: true)
                .proLock(!store.isUnlocked(.replayBuffer))
        }
        .barMenuStyle()
        .help("Save Recent (⇧⌘R = last 30 s)")
    }
}

#if os(macOS)
private struct GoLiveButton: View {
    let layout: ControlBarLayout
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @Environment(EntitlementStore.self) private var store

    var body: some View {
        let streamingHere = app.streamer.state.isActive && app.streamer.deviceID == viewer.deviceID
        Button {
            guard store.gate(.streaming) else { return }
            if app.streamer.state.isActive {
                app.stopStreaming()
            } else {
                app.viewerBecameActive(viewer)
                app.isShowingStreamSetup = true
            }
        } label: {
            BarLabel(symbol: Symbol.goLive, title: streamingHere ? "Live" : "Go Live",
                     showsTitle: layout.showsLabels, tint: streamingHere ? .red : nil)
                .proLock(!store.isUnlocked(.streaming))
        }
        .buttonStyle(BarButtonStyle())
        .help(app.streamer.state.isActive ? "Stop Streaming (⇧⌘L)" : "Go Live (⇧⌘L)")
    }
}
#endif

private struct RecordButton: View {
    let layout: ControlBarLayout
    @Environment(ViewerModel.self) private var viewer
    @Environment(EntitlementStore.self) private var store

    var body: some View {
        let since = viewer.session?.recordingSince
        Button {
            guard store.gate(.recording) else { return }
            viewer.toggleRecording()
        } label: {
            HStack(spacing: 6) {
                if let since {
                    RoundedRectangle(cornerRadius: 2).fill(.white).frame(width: 9, height: 9)
                    TimelineView(.periodic(from: since, by: 1)) { context in
                        let s = Int(context.date.timeIntervalSince(since))
                        Text(s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
                                       : String(format: "%d:%02d", s / 60, s % 60))
                            .font(.system(size: 12, weight: .semibold).monospacedDigit())
                    }
                } else {
                    Circle().fill(.red).frame(width: 10, height: 10)
                    if layout.showsLabels {
                        Text("Record").font(.system(size: 12, weight: .semibold))
                    }
                }
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 10)
            .frame(minWidth: barTarget, minHeight: barTarget)
            .background(Capsule().fill(since != nil ? Color.red : .white.opacity(0.14)))
            .proLock(!store.isUnlocked(.recording))
        }
        .buttonStyle(.plain)
        // The test pattern isn't recordable; say so instead of failing after the click.
        .disabled(viewer.testPattern != nil && since == nil)
        .opacity(viewer.testPattern != nil && since == nil ? 0.45 : 1)
        .help(viewer.testPattern != nil ? "Switch to the live input to record"
              : since != nil ? "Stop Recording (⌘R)" : "Record (⌘R)")
        .accessibilityLabel(since != nil ? "Stop recording" : "Record")
    }
}

private struct OptionsMenu: View {
    let layout: ControlBarLayout
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        @Bindable var app = app
        @Bindable var viewer = viewer
        Menu {
            if layout == .minimal {
                Toggle(isOn: $viewer.preset.muted) { Label("Mute", systemImage: Symbol.mute) }
                Button { viewer.saveScreenshot() } label: { Label("Save Screenshot", systemImage: Symbol.screenshot) }
                #if os(macOS)
                Button { viewer.toggleFullScreen() } label: {
                    Label(viewer.isFullScreen ? "Exit Full Screen" : "Full Screen",
                          systemImage: viewer.isFullScreen ? Symbol.exitFullScreen : Symbol.fullScreen)
                }
                #endif
                Divider()
            }
            Picker(selection: $viewer.preset.scale) {
                ForEach(ScaleMode.allCases, id: \.self) { Label($0.title, systemImage: $0.symbol).tag($0) }
            } label: {
                Label("Scale", systemImage: Symbol.scale)
            }
            Menu { PictureLookItems(viewer: viewer) } label: { Label("Picture Look", systemImage: Symbol.pictureLook) }
            Toggle(isOn: Binding(
                get: { viewer.preset.filter == .nearest },
                set: { viewer.preset.filter = $0 ? .nearest : .linear }
            )) {
                Label("Sharp Pixels", systemImage: Symbol.sharpPixels)
            }
            Menu { GuideItems(app: app) } label: { Label("Guides", systemImage: Symbol.guides) }
            Divider()
            Toggle(isOn: $app.prefs.showStats) { Label("Show Diagnostics", systemImage: Symbol.stats) }
            #if os(iOS)
            Button { app.isShowingSettings = true } label: { Label("Settings…", systemImage: Symbol.settings) }
            #endif
            Button { app.isShowingHelp = true } label: { Label("Connection Help…", systemImage: "questionmark.circle") }
            #if os(macOS)
            Toggle(isOn: $app.prefs.floatOnTop) { Label("Float on Top", systemImage: Symbol.floatOnTop) }
            Toggle(isOn: $app.prefs.cleanMode) { Label("Clean Mode", systemImage: Symbol.cleanMode) }
            Button { openWindow(id: "scopes") } label: { Label("Show Scopes", systemImage: Symbol.scopes) }
            #endif
            Divider()
            Button { viewer.copyFrame() } label: { Label("Copy Frame", systemImage: Symbol.copyFrame) }
        } label: {
            BarLabel(symbol: Symbol.options, title: "View options", showsTitle: false)
        }
        .barMenuStyle()
        .help("View options")
    }
}

#if os(macOS)
private struct FullScreenButton: View {
    @Environment(ViewerModel.self) private var viewer

    var body: some View {
        Button { viewer.toggleFullScreen() } label: {
            BarLabel(symbol: viewer.isFullScreen ? Symbol.exitFullScreen : Symbol.fullScreen,
                     title: viewer.isFullScreen ? "Exit full screen" : "Full screen", showsTitle: false)
        }
        .buttonStyle(BarButtonStyle())
        .help(viewer.isFullScreen ? "Exit Full Screen (⌘F)" : "Full Screen (⌘F)")
    }
}
#endif

// MARK: - Building blocks

/// Icon (+ optional title and ▾) in the bar's type style.
/// Hit-target size: compact on the Mac (pointer), 44 pt on iPad (touch, HIG minimum).
private var barTarget: CGFloat {
    #if os(iOS)
    44
    #else
    28
    #endif
}

private struct BarLabel: View {
    let symbol: String
    let title: String?
    var showsTitle = true
    var chevron = false
    var tint: Color?

    var body: some View {
        HStack(spacing: 5) {
            Image(systemName: symbol)
                .font(.system(size: 14, weight: .medium))
                .frame(width: 18)
                .foregroundStyle(tint ?? .white)
            if showsTitle, let title {
                Text(title)
                    .font(.system(size: 12, weight: .medium))
                    .lineLimit(1)
            }
            if chevron {
                Image(systemName: "chevron.down")
                    .font(.system(size: 8, weight: .bold))
                    .opacity(0.6)
            }
        }
        .foregroundStyle(.white)
        .shadow(color: .black.opacity(0.5), radius: 1.5)
        .padding(.horizontal, 7)
        .frame(minWidth: barTarget, minHeight: barTarget)
        .contentShape(Rectangle())
        // The title is read even when the compact bar hides it.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(title ?? "")
    }
}

private struct BarDivider: View {
    var body: some View {
        Rectangle().fill(.white.opacity(0.18)).frame(width: 1, height: 18).padding(.horizontal, 3)
    }
}

/// Plain bar item with a soft highlight on hover and press.
private struct BarButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        BarButtonBody(configuration: configuration)
    }

    private struct BarButtonBody: View {
        let configuration: Configuration
        @State private var hovered = false
        @Environment(\.isEnabled) private var isEnabled

        var body: some View {
            configuration.label
                .background(RoundedRectangle(cornerRadius: 7).fill(.white.opacity(configuration.isPressed ? 0.22 : hovered ? 0.12 : 0)))
                .opacity(isEnabled ? 1 : 0.4)
                .onHover { hovered = $0 }
        }
    }
}

private extension View {
    func barMenuStyle() -> some View {
        menuStyle(.button)
            .buttonStyle(BarButtonStyle())
            .menuIndicator(.hidden)
            .fixedSize()
    }

    /// Small lock on Pro controls the user hasn't unlocked; clicking still opens the upgrade sheet.
    func proLock(_ locked: Bool) -> some View {
        overlay(alignment: .topTrailing) {
            if locked {
                Image(systemName: Symbol.locked)
                    .font(.system(size: 7, weight: .bold))
                    .foregroundStyle(.white)
                    .padding(2)
                    .background(Circle().fill(.orange))
                    .offset(x: 3, y: -2)
            }
        }
    }
}
