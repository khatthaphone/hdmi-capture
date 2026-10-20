import CaptureCore
import SwiftUI

struct ContentView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ZStack {
            Color.black
            if let renderer = model.renderer {
                MetalPreview(renderer: renderer) { model.toggleFullScreen() }
            }
            if model.phase != .live {
                StatusOverlay()
            }
            if model.prefs.showStats, model.phase == .live {
                StatsOverlay()
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                    .padding(12)
                    .allowsHitTesting(false)
            }
        }
        .ignoresSafeArea()
        .frame(minWidth: 320, minHeight: 180)
        .background(WindowConfigurator(prefs: model.prefs, aspect: model.aspect) { model.window = $0 })
    }
}

private struct StatusOverlay: View {
    @Environment(AppModel.self) private var model

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
        switch model.phase {
        case .permissionNeeded:
            Label("Camera access needed", systemImage: "video.badge.ellipsis").font(.title2)
            Text("HDMI capture devices appear to macOS as cameras.").foregroundStyle(.secondary)
        case .permissionDenied:
            Label("Camera access is off", systemImage: "video.slash").font(.title2)
            Text("Allow HDMI Captain under Privacy & Security → Camera, then try again.").foregroundStyle(.secondary)
            HStack {
                Button("Open Privacy Settings") { model.openPrivacySettings() }
                Button("Try Again") { model.retry() }
            }
        case .scanning:
            ProgressView().controlSize(.large).tint(.white)
            Text("Scanning for hardware").font(.title2)
            Text("Connect an HDMI capture device.").foregroundStyle(.secondary)
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
            Button("Try Again") { model.retry() }
        case .live:
            EmptyView()
        }
    }
}

private struct StatsOverlay: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let info = model.liveInfo {
            VStack(alignment: .leading, spacing: 2) {
                Text("\(info.deviceName)")
                Text("\(info.format.width)×\(info.format.height) \(info.format.fourCC) @ \(info.frameRate, specifier: "%.2f")")
                if let size = model.stats.frameSize {
                    Text("frames \(Int(size.width))×\(Int(size.height))")
                }
                Text("in \(model.stats.inputFPS, specifier: "%.1f") fps · out \(model.stats.renderFPS, specifier: "%.1f") fps · dropped \(model.stats.dropped)")
                Text("audio: \(info.audioDeviceName ?? "none")\(model.preset.muted ? " (muted)" : "")")
            }
            .font(.system(size: 11, design: .monospaced))
            .foregroundStyle(.white)
            .padding(8)
            .background(.black.opacity(0.6), in: RoundedRectangle(cornerRadius: 6))
        }
    }
}
