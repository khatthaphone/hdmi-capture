import CaptureCore
import SwiftUI

/// Menu-bar extra: quick control while the window is hidden or the app runs menu-bar only.
struct MenuBarPanel: View {
    @Environment(AppModel.self) private var app
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        @Bindable var app = app
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: session?.state == .live ? "circle.fill" : "circle")
                    .foregroundStyle(session?.state == .live ? .green : .secondary)
                    .font(.caption)
                Text(status).font(.headline).lineLimit(1)
            }

            if !app.catalog.videoDevices.isEmpty {
                Picker("Input", selection: Binding(
                    get: { app.mainDeviceID ?? "" },
                    set: { if !$0.isEmpty { app.selectVideo($0) } }
                )) {
                    ForEach(app.catalog.videoDevices) { Text($0.name).tag($0.id) }
                }
                .labelsHidden()
            }

            if let session {
                @Bindable var session = session
                HStack {
                    Button {
                        session.preset.muted.toggle()
                    } label: {
                        Image(systemName: session.preset.muted ? Symbol.mute : Symbol.sound)
                            .frame(width: 18)
                    }
                    .buttonStyle(.borderless)
                    Slider(value: $session.preset.volume, in: 0...1)
                        .disabled(session.preset.muted)
                }
            }

            Toggle("Float on top", isOn: $app.prefs.floatOnTop)
            Toggle("Menu bar only (hide Dock icon)", isOn: $app.prefs.menuBarOnly)

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

    private var session: DeviceSession? {
        app.mainDeviceID.flatMap { app.sessions[$0] }
    }

    private var status: String {
        guard app.permission == .granted else { return "Camera access needed" }
        guard let session else { return "No capture card" }
        switch session.state {
        case .live: return session.liveInfo.map { "\(session.deviceName) · \($0.format.width)×\($0.format.height)" } ?? session.deviceName
        case .idle, .connecting: return "Connecting to \(session.deviceName)"
        case let .recovering(reason): return reason
        case let .paused(reason): return reason.message
        case let .failed(message): return message
        case .absent: return "\(session.deviceName) disconnected"
        }
    }
}
