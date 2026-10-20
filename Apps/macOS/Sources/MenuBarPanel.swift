import CaptureCore
import SwiftUI

/// Menu-bar extra: quick control while the window is hidden or the app runs menu-bar only.
struct MenuBarPanel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        @Bindable var model = model
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: model.phase == .live ? "circle.fill" : "circle")
                    .foregroundStyle(model.phase == .live ? .green : .secondary)
                    .font(.caption)
                Text(status).font(.headline).lineLimit(1)
            }

            if !model.catalog.videoDevices.isEmpty {
                Picker("Input", selection: Binding(
                    get: { model.selectedVideoID ?? "" },
                    set: { if !$0.isEmpty { model.selectVideo($0) } }
                )) {
                    ForEach(model.catalog.videoDevices) { Text($0.name).tag($0.id) }
                }
                .labelsHidden()
            }

            HStack {
                Button {
                    model.preset.muted.toggle()
                } label: {
                    Image(systemName: model.preset.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                        .frame(width: 18)
                }
                .buttonStyle(.borderless)
                Slider(value: $model.preset.volume, in: 0...1)
                    .disabled(model.preset.muted)
            }

            Toggle("Float on top", isOn: $model.prefs.floatOnTop)
            Toggle("Menu bar only (hide Dock icon)", isOn: $model.prefs.menuBarOnly)

            Divider()
            HStack {
                Button("Show Window") {
                    openWindow(id: "main")
                    NSApp.activate()
                }
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }
            }
        }
        .padding(14)
        .frame(width: 280)
    }

    private var status: String {
        switch model.phase {
        case .live: model.liveInfo.map { "\($0.deviceName) · \($0.format.width)×\($0.format.height)" } ?? "Live"
        case .scanning: "No capture device"
        case let .connecting(name): "Connecting to \(name)"
        case let .recovering(reason): reason
        case .permissionNeeded, .permissionDenied: "Camera access needed"
        case let .failed(message): message
        }
    }
}
