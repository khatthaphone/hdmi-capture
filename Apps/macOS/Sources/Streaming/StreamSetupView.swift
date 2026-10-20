import SwiftUI

/// Go Live sheet: destination, key (Keychain), quality.
struct StreamSetupView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var key = ""

    var body: some View {
        @Bindable var app = app
        VStack(alignment: .leading, spacing: 16) {
            Label("Go Live", systemImage: "dot.radiowaves.left.and.right").font(.title2.bold())
            Form {
                Picker("Service", selection: Binding(
                    get: { app.prefs.stream.service },
                    set: { service in
                        app.prefs.stream.service = service
                        if service != .custom { app.prefs.stream.server = service.defaultServer }
                    }
                )) {
                    ForEach(StreamSettings.Service.allCases) { Text($0.title).tag($0) }
                }
                TextField("Server", text: $app.prefs.stream.server, prompt: Text("rtmp://server/app or srt://host:port"))
                    .disabled(app.prefs.stream.service != .custom)
                SecureField("Stream key", text: $key)
                Picker("Resolution", selection: $app.prefs.stream.resolution) {
                    ForEach(StreamSettings.Resolution.allCases) { Text($0.title).tag($0) }
                }
                Slider(value: Binding(get: { Double(app.prefs.stream.videoBitrate) }, set: { app.prefs.stream.videoBitrate = Int($0) }),
                       in: 2500...12000, step: 500) {
                    Text("Bitrate \(app.prefs.stream.videoBitrate / 1000).\(app.prefs.stream.videoBitrate % 1000 / 100) Mbps")
                }
            }
            .formStyle(.grouped)
            Text("The stream key is stored in your Keychain only. YouTube recommends 4.5–9 Mbps for 1080p, Twitch up to 6 Mbps.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Go Live") {
                    StreamKeyStore.setKey(key, for: AppModel.streamKeyAccount)
                    dismiss()
                    app.startStreaming()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(key.isEmpty || app.prefs.stream.publishURL(key: key) == nil)
            }
        }
        .padding(20)
        .frame(width: 480)
        .onAppear { key = StreamKeyStore.key(for: AppModel.streamKeyAccount) ?? "" }
    }
}
