import CaptureCore
import SwiftUI

/// All connected cards side by side. Click a tile to hear it; double-click to open it in its own window.
struct MultiViewGrid: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        let devices = app.catalog.videoDevices
        Group {
            if devices.isEmpty {
                ContentUnavailableView("No capture cards", systemImage: "rectangle.on.rectangle.slash",
                                       description: Text("Connect capture cards to see them here."))
            } else {
                let columns = devices.count == 1 ? 1 : devices.count <= 4 ? 2 : 3
                Grid(horizontalSpacing: 2, verticalSpacing: 2) {
                    ForEach(Array(stride(from: 0, to: devices.count, by: columns)), id: \.self) { start in
                        GridRow {
                            ForEach(devices[start..<min(start + columns, devices.count)]) { device in
                                TileRoot(app: app, deviceID: device.id)
                                    .id(device.id)
                            }
                        }
                    }
                }
            }
        }
        .background(.black)
        .frame(minWidth: 480, minHeight: 270)
    }
}

/// Owns the tile's pinned `ViewerModel`.
private struct TileRoot: View {
    @State private var viewer: ViewerModel

    init(app: AppModel, deviceID: String) {
        _viewer = State(initialValue: ViewerModel(app: app, source: .pinned(deviceID)))
    }

    var body: some View {
        Tile()
            .environment(viewer)
            .onAppear { viewer.activate() }
            .onDisappear { viewer.deactivate() }
    }
}

private struct Tile: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        ZStack {
            Color.black
            if let renderer = viewer.renderer {
                MetalPreview(renderer: renderer, autoHidesWindowButtons: false, onClick: focus) {
                    if let id = viewer.deviceID { openWindow(id: "card", value: id) }
                }
            }
            if viewer.phase != .live {
                Text(status)
                    .font(.callout)
                    .foregroundStyle(.white.opacity(0.8))
                    .padding(8)
                    .background(.black.opacity(0.6), in: RoundedRectangle(cornerRadius: 6))
                    .allowsHitTesting(false)
            }
            VStack {
                Spacer()
                HStack(spacing: 6) {
                    Image(systemName: viewer.hasAudioFocus ? "speaker.wave.2.fill" : "speaker.slash")
                    Text(viewer.session?.deviceName ?? app.knownName(for: viewer.deviceID ?? ""))
                        .lineLimit(1)
                    if let info = viewer.liveInfo {
                        Text("\(info.format.width)×\(info.format.height)").foregroundStyle(.secondary)
                    }
                    Spacer()
                    if viewer.isDroppingFrames {
                        Label("Dropping frames — try another USB port", systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                            .lineLimit(1)
                    }
                    if viewer.session?.isRecording == true {
                        Circle().fill(.red).frame(width: 8, height: 8)
                    }
                }
                .font(.caption)
                .foregroundStyle(.white)
                .padding(.horizontal, 8)
                .padding(.vertical, 5)
                .background(.black.opacity(0.55))
            }
            .allowsHitTesting(false)
        }
        .overlay {
            Rectangle()
                .strokeBorder(viewer.hasAudioFocus ? Color.accentColor : .clear, lineWidth: 2)
                .allowsHitTesting(false)
        }
        .aspectRatio(16 / 9, contentMode: .fit)
    }

    private func focus() {
        if let id = viewer.deviceID { app.setAudioFocus(id) }
    }

    private var status: String {
        switch viewer.phase {
        case let .connecting(name): "Connecting to \(name)…"
        case let .waiting(name): "\(name) not connected"
        case let .recovering(reason): reason
        case let .paused(reason): reason.message
        case let .failed(message): message
        case .permissionNeeded, .permissionDenied: "Camera access needed"
        case .scanning, .live: ""
        }
    }
}
