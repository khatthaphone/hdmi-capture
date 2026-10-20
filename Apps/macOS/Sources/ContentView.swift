import CaptureCore
import SwiftUI

struct ContentView: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        ZStack {
            Color.black
            if let renderer = viewer.renderer {
                MetalPreview(renderer: renderer, autoHidesWindowButtons: !app.prefs.cleanMode) { viewer.toggleFullScreen() }
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
            if let since = viewer.session?.recordingSince, app.prefs.showRecordingIndicator {
                RecordingIndicator(since: since)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
                    .padding(12)
                    .allowsHitTesting(false)
            }
            if let toast = viewer.toast {
                Banner(text: toast, symbol: "checkmark.circle.fill")
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
                    .padding(.bottom, 20)
                    .allowsHitTesting(false)
                    .transition(.opacity)
            }
            if app.prefs.showStats, viewer.phase == .live {
                StatsOverlay()
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                    .padding(12)
                    .allowsHitTesting(false)
            }
        }
        .ignoresSafeArea()
        .frame(minWidth: 320, minHeight: 180)
        .background(WindowConfigurator(prefs: app.prefs, aspect: viewer.aspect) { viewer.window = $0 })
        #if DEBUG
        .task {
            // QA: `-qaAction openCards` opens a window per connected card.
            guard viewer.source == .automatic, UserDefaults.standard.string(forKey: "qaAction") == "openCards" else { return }
            try? await Task.sleep(for: .seconds(2))
            app.catalog.videoDevices.forEach { openWindow(id: "card", value: $0.id) }
        }
        .task {
            guard viewer.source == .automatic, UserDefaults.standard.string(forKey: "qaAction") == "grid" else { return }
            try? await Task.sleep(for: .seconds(2))
            openWindow(id: "grid")
        }
        #endif
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
            return .banner("\(reason) — reconnecting…")
        default:
            return .full
        }
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

private struct NoSignalOverlay: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: "rectangle.slash").font(.system(size: 40))
            Text("No signal").font(.title2)
            Text(hint)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 420)
        }
        .foregroundStyle(.white)
        .multilineTextAlignment(.center)
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.black.opacity(0.55))
        .allowsHitTesting(false)
    }

    private var hint: String {
        switch viewer.signal {
        case .black:
            "The capture card sends a black picture. Check that the source is on and outputting video. Sources that use HDCP (PlayStation, Apple TV, streaming apps) can't be captured — turn HDCP off in the source's settings."
        default:
            "The capture card is showing its own screen. Check the HDMI cable and that the source is on."
        }
    }
}

private struct StatusOverlay: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app

    var body: some View {
        VStack(spacing: 12) {
            content
        }
        .foregroundStyle(.white)
        .multilineTextAlignment(.center)
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.black.opacity(0.65))
    }

    @ViewBuilder private var content: some View {
        switch viewer.phase {
        case .permissionNeeded:
            Label("Camera access needed", systemImage: "video.badge.ellipsis").font(.title2)
            Text("HDMI capture devices appear to macOS as cameras.").foregroundStyle(.secondary)
        case .permissionDenied:
            Label("Camera access is off", systemImage: "video.slash").font(.title2)
            Text("Allow HDMI Captain under Privacy & Security → Camera, then try again.").foregroundStyle(.secondary)
            HStack {
                Button("Open Privacy Settings") { app.openPrivacySettings() }
                Button("Try Again") { app.retry() }
            }
        case .scanning:
            ProgressView().controlSize(.large).tint(.white)
            Text("Scanning for hardware").font(.title2)
            Text("Connect an HDMI capture device.").foregroundStyle(.secondary)
        case let .waiting(name):
            Image(systemName: "cable.connector.slash").font(.system(size: 36))
            Text("\(name) is not connected").font(.title2)
            Text("The picture comes back as soon as it's plugged in.").foregroundStyle(.secondary)
        case let .connecting(name):
            ProgressView().controlSize(.large).tint(.white)
            Text("Connecting to \(name)").font(.title2)
        case let .recovering(reason):
            ProgressView().controlSize(.large).tint(.white)
            Text(reason).font(.title2)
            Text("Reconnecting automatically…").foregroundStyle(.secondary)
        case let .failed(message):
            Label("Can't show video", systemImage: "exclamationmark.triangle").font(.title2)
            Text(message).foregroundStyle(.secondary)
            Button("Try Again") { app.retry() }
        case .live:
            EmptyView()
        }
    }
}

private struct StatsOverlay: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app

    var body: some View {
        if let info = viewer.liveInfo {
            VStack(alignment: .leading, spacing: 2) {
                Text("\(info.deviceName)")
                Text("requested \(info.format.width)×\(info.format.height) \(info.format.fourCC) @ \(info.frameRate, specifier: "%.2f")")
                if let size = viewer.stats.frameSize {
                    let mismatch = Int(size.width) != info.format.width || Int(size.height) != info.format.height
                    Text("actual \(Int(size.width))×\(Int(size.height))\(mismatch ? "  ⚠︎ differs" : "")")
                        .foregroundStyle(mismatch ? .orange : .white)
                }
                Text("in \(viewer.stats.inputFPS, specifier: "%.1f") fps · out \(viewer.stats.renderFPS, specifier: "%.1f") fps · dropped \(viewer.stats.dropped)")
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

    var body: some View {
        TimelineView(.periodic(from: since, by: 1)) { context in
            let elapsed = Int(context.date.timeIntervalSince(since))
            HStack(spacing: 6) {
                Circle().fill(.red).frame(width: 8, height: 8)
                Text(String(format: "REC %d:%02d:%02d", elapsed / 3600, elapsed / 60 % 60, elapsed % 60))
                    .font(.system(size: 11, weight: .semibold, design: .monospaced))
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(.black.opacity(0.6), in: Capsule())
        }
    }
}
