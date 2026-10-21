import CaptureCore
import SwiftUI

/// A viewer (main window, card window, or the iPad's window) and its model's lifecycle.
struct ViewerRoot: View {
    @State private var viewer: ViewerModel

    init(app: AppModel, source: ViewerModel.Source) {
        _viewer = State(initialValue: ViewerModel(app: app, source: source))
    }

    var body: some View {
        ContentView()
            .environment(viewer)
            .onAppear { viewer.activate() }
            .onDisappear { viewer.deactivate() }
    }
}

struct ContentView: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @Environment(\.openWindow) private var openWindow
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        ZStack {
            Color.black
            if let renderer = viewer.renderer {
                MetalPreview(
                    renderer: renderer,
                    autoHidesWindowButtons: !app.prefs.cleanMode,
                    keepsChromeVisible: showsControls,
                    onClick: tapAction,
                    onPointer: { viewer.pointerMoved(fromBottom: $0, height: $1) }
                ) { viewer.toggleFullScreen() }
            }
            if !app.prefs.guides.isEmpty, let source = viewer.sourceSize, viewer.phase == .live || viewer.testPattern != nil {
                GuidesOverlay(guides: app.prefs.guides, source: source, scale: viewer.preset.scale)
                    .allowsHitTesting(false)
            }
            switch overlay {
            case .none:
                EmptyView()
            case .full:
                StatusOverlay()
            case .noSignal:
                NoSignalOverlay()
            case let .banner(text):
                Banner(text: text)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .padding(.top, app.prefs.cleanMode ? 12 : 40)
                    .allowsHitTesting(false)
            }
            if app.microphoneDenied, viewer.phase == .live {
                MicrophoneBanner()
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .padding(.top, app.prefs.cleanMode ? 12 : 40)
            }
            VStack(alignment: .trailing, spacing: 6) {
                // Always visible while recording; the pref only hides the timer, never the red dot.
                if let since = viewer.session?.recordingSince {
                    RecordingIndicator(since: since, showsTimer: app.prefs.showRecordingIndicator)
                }
                #if os(macOS)
                if app.streamer.deviceID != nil, app.streamer.deviceID == viewer.deviceID {
                    StreamIndicator(state: app.streamer.state)
                }
                #endif
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
            .padding(12)
            .allowsHitTesting(false)
            if let toast = viewer.toast {
                ToastView(feedback: toast)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
                    .padding(.bottom, showsControls ? 66 : 20)
                    .allowsHitTesting(false)
                    .transition(.opacity)
            }
            if app.prefs.showStats, viewer.phase == .live {
                StatsOverlay()
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                    .padding(12)
                    .padding(.bottom, showsControls ? 50 : 0)
                    .allowsHitTesting(false)
            }
            if showsControls {
                ControlBar()
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
                    .padding(.bottom, 14)
                    .transition(.opacity.combined(with: .offset(y: 8)))
            }
        }
        .animation(showsControls ? .easeOut(duration: 0.15) : .easeIn(duration: 0.3), value: showsControls)
        .ignoresSafeArea()
        .frame(minWidth: 320, minHeight: 180)
        #if os(iOS)
        // Nothing to draw in the background; capture stays configured and resumes instantly.
        .onChange(of: scenePhase) { _, phase in viewer.renderer?.setVisible(phase != .background) }
        #endif
        #if os(macOS)
        .background(WindowConfigurator(prefs: app.prefs, aspect: viewer.aspect) { viewer.window = $0 })
        #endif
        #if DEBUG && os(macOS)
        .task {
            // QA: `-qaAction openCards` opens a window per connected card.
            guard viewer.source == .automatic, UserDefaults.standard.string(forKey: "qaAction") == "openCards" else { return }
            try? await Task.sleep(for: .seconds(2))
            app.catalog.videoDevices.forEach { openWindow(id: "card", value: $0.id) }
        }
        .task {
            guard viewer.source == .automatic, let action = UserDefaults.standard.string(forKey: "qaAction"),
                  ["grid", "scopes"].contains(action) else { return }
            try? await Task.sleep(for: .seconds(2))
            openWindow(id: action)
        }
        #endif
    }

    /// Mac: a click drags the window. iPad: a tap shows or hides the controls.
    private var tapAction: (() -> Void)? {
        #if os(macOS)
        nil
        #else
        { viewer.toggleControls() }
        #endif
    }

    private var showsControls: Bool {
        app.prefs.showControlsOnHover && viewer.controlsVisible
    }

    private enum Overlay: Equatable {
        case none, full, noSignal
        case banner(String)
    }

    /// Prefer keeping the last picture on screen with a small banner; cover it only when
    /// there is nothing meaningful to show.
    private var overlay: Overlay {
        if viewer.testPattern != nil { return .none }
        switch viewer.phase {
        case .live:
            if viewer.signal.isNoSignal { return .noSignal }
            if viewer.signalInterrupted { return .banner("Signal interrupted") }
            return .none
        case let .recovering(reason) where viewer.hasFrame:
            return .banner(StatusCopy.recoveringBanner(reason))
        default:
            return .full
        }
    }
}

/// Bottom feedback: icon and tint follow the kind, so an error never looks like a success.
private struct ToastView: View {
    let feedback: Feedback

    var body: some View {
        HStack(spacing: 8) {
            switch feedback.kind {
            case .progress:
                ProgressView().controlSize(.small).tint(.white)
            case .success:
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
            case .info:
                Image(systemName: "info.circle.fill").foregroundStyle(.white.opacity(0.85))
            case .warning:
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
            case .error:
                Image(systemName: "xmark.octagon.fill").foregroundStyle(.red)
            }
            Text(feedback.text)
        }
        .font(.callout.weight(.medium))
        .foregroundStyle(.white)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(.black.opacity(0.75), in: Capsule())
        .overlay(Capsule().stroke(.white.opacity(0.12), lineWidth: 1))
        .accessibilityElement(children: .combine)
    }
}

/// Video works without microphone access but has no sound; say why and how to fix it.
private struct MicrophoneBanner: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        HStack(spacing: 10) {
            Label("No sound: microphone access is off", systemImage: "speaker.slash.fill")
            Button("Open Settings") { app.openPrivacySettings(microphone: true) }
                .buttonStyle(.bordered)
                .controlSize(.small)
        }
        .font(.callout.weight(.medium))
        .foregroundStyle(.white)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(.black.opacity(0.75), in: Capsule())
    }
}

private struct Banner: View {
    let text: String
    var symbol = "exclamationmark.triangle.fill"

    var body: some View {
        Label(text, systemImage: symbol)
            .font(.callout.weight(.medium))
            .foregroundStyle(.white)
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .background(.black.opacity(0.7), in: Capsule())
    }
}

private struct StatsOverlay: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app

    var body: some View {
        if let info = viewer.liveInfo {
            VStack(alignment: .leading, spacing: 2) {
                Text("\(info.deviceName)")
                // Verbatim: technical values, not locale-formatted ("1.920×1.080" on some iPads).
                Text(verbatim: "requested \(info.format.width)×\(info.format.height) \(info.format.fourCC) @ \(String(format: "%.2f", info.frameRate))")
                if let size = viewer.stats.frameSize {
                    let mismatch = Int(size.width) != info.format.width || Int(size.height) != info.format.height
                    Text(verbatim: "actual \(Int(size.width))×\(Int(size.height))\(mismatch ? "  ⚠︎ differs" : "")")
                        .foregroundStyle(mismatch ? .orange : .white)
                }
                Text(verbatim: String(format: "in %.1f fps · out %.1f fps · dropped %llu", viewer.stats.inputFPS, viewer.stats.renderFPS, viewer.stats.dropped))
                if let capture = viewer.stats.captureLatency {
                    let display = viewer.stats.displayLatency
                    Text(String(format: "latency: capture %.0f ms · on screen ≈ %@", capture.averageMs,
                                display.map { String(format: "%.0f ms (p95 %.0f)", $0.averageMs, $0.p95Ms) } ?? "—"))
                }
                Text("audio: \(info.audioDeviceName ?? "none")\(viewer.preset.muted ? " (muted)" : "") · delay \(viewer.preset.audioDelayMs) ms")
            }
            .font(.system(size: 11, design: .monospaced))
            .foregroundStyle(.white)
            .padding(8)
            .background(.black.opacity(0.6), in: RoundedRectangle(cornerRadius: 6))
        }
    }
}

/// Thirds, center cross, safe areas and aspect masks over the visible video rect.
private struct GuidesOverlay: View {
    let guides: GuideSettings
    let source: CGSize
    let scale: ScaleMode

    var body: some View {
        Canvas { context, size in
            let video = RenderGeometry.compute(source: source, drawable: size, mode: scale).viewport
            let line = Color.white.opacity(guides.opacity)
            func stroke(_ path: Path, dash: [CGFloat] = []) {
                context.stroke(path, with: .color(.black.opacity(guides.opacity * 0.6)), style: StrokeStyle(lineWidth: 3, dash: dash))
                context.stroke(path, with: .color(line), style: StrokeStyle(lineWidth: 1, dash: dash))
            }
            if let mask = guides.mask {
                let visible = GuideSettings.maskRect(mask, in: video)
                var outside = Path(video)
                outside.addRect(visible)
                context.fill(outside, with: .color(.black.opacity(0.5 + guides.opacity * 0.4)), style: FillStyle(eoFill: true))
                stroke(Path(visible))
            }
            if guides.thirds {
                var path = Path()
                for i in 1...2 {
                    let x = video.minX + video.width * CGFloat(i) / 3
                    let y = video.minY + video.height * CGFloat(i) / 3
                    path.move(to: CGPoint(x: x, y: video.minY)); path.addLine(to: CGPoint(x: x, y: video.maxY))
                    path.move(to: CGPoint(x: video.minX, y: y)); path.addLine(to: CGPoint(x: video.maxX, y: y))
                }
                stroke(path)
            }
            if guides.center {
                let arm = min(video.width, video.height) * 0.04
                var path = Path()
                path.move(to: CGPoint(x: video.midX - arm, y: video.midY)); path.addLine(to: CGPoint(x: video.midX + arm, y: video.midY))
                path.move(to: CGPoint(x: video.midX, y: video.midY - arm)); path.addLine(to: CGPoint(x: video.midX, y: video.midY + arm))
                stroke(path)
            }
            if guides.safeAreas {
                stroke(Path(video.insetBy(dx: video.width * 0.05, dy: video.height * 0.05)), dash: [6, 4])  // action safe 90 %
                stroke(Path(video.insetBy(dx: video.width * 0.10, dy: video.height * 0.10)), dash: [2, 3])  // title safe 80 %
            }
        }
    }
}

private struct RecordingIndicator: View {
    let since: Date
    var showsTimer = true

    var body: some View {
        TimelineView(.periodic(from: since, by: 1)) { context in
            let elapsed = Int(context.date.timeIntervalSince(since))
            HStack(spacing: 6) {
                Circle().fill(.red).frame(width: 8, height: 8)
                Text(showsTimer ? String(format: "REC %d:%02d:%02d", elapsed / 3600, elapsed / 60 % 60, elapsed % 60) : "REC")
                    .font(.system(size: 11, weight: .semibold, design: .monospaced))
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Recording")
            .foregroundStyle(.white)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(.black.opacity(0.6), in: Capsule())
        }
    }
}

#if os(macOS)
private struct StreamIndicator: View {
    let state: Streamer.State

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            HStack(spacing: 6) {
                Circle().fill(color).frame(width: 8, height: 8)
                Text(label(now: context.date)).font(.system(size: 11, weight: .semibold, design: .monospaced))
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(.black.opacity(0.6), in: Capsule())
        }
    }

    private var color: Color {
        switch state {
        case .live: .red
        case .connecting, .reconnecting: .orange
        default: .gray
        }
    }

    private func label(now: Date) -> String {
        switch state {
        case let .live(since):
            let s = Int(now.timeIntervalSince(since))
            return String(format: "LIVE %d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
        case .connecting: return "CONNECTING…"
        case let .reconnecting(attempt): return "RECONNECTING (\(attempt))"
        case let .failed(message): return message
        case .idle: return ""
        }
    }
}
#endif
