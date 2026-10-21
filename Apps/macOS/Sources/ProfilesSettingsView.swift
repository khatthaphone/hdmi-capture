import CaptureCore
import SwiftUI
import UniformTypeIdentifiers

/// Settings → Profiles: apply, rename, change icon, delete, import/export.
struct ProfilesSettingsView: View {
    @Environment(AppModel.self) private var app
    @State private var message: String?

    private static let symbols = ["gamecontroller", "gamecontroller.fill", "arcade.stick", "playstation.logo", "xbox.logo",
                                  "camera", "video", "desktopcomputer", "laptopcomputer", "tv", "star", "sparkles"]

    var body: some View {
        let session = app.currentViewer?.session
        Form {
            Section {
                ForEach(app.profiles) { profile in
                    HStack {
                        if profile.isBuiltIn {
                            Image(systemName: profile.symbol).frame(width: 24)
                            Text(profile.name)
                            Text("Built-in").font(.caption).foregroundStyle(.secondary)
                        } else {
                            Menu {
                                ForEach(Self.symbols, id: \.self) { symbol in
                                    Button { update(profile) { $0.symbol = symbol } } label: { Label(symbol, systemImage: symbol) }
                                }
                            } label: {
                                Image(systemName: profile.symbol)
                            }
                            .menuStyle(.borderlessButton)
                            .frame(width: 36)
                            TextField("Name", text: Binding(get: { profile.name }, set: { name in update(profile) { $0.name = name } }))
                                .textFieldStyle(.roundedBorder)
                        }
                        Spacer()
                        if session?.preset.profileID == profile.id {
                            Image(systemName: "checkmark").foregroundStyle(.secondary)
                        }
                        Button("Apply") { session?.applyProfile(profile) }
                            .disabled(session == nil)
                        if !profile.isBuiltIn {
                            Button(role: .destructive) { app.deleteProfile(profile.id) } label: { Image(systemName: "trash") }
                                .buttonStyle(.borderless)
                        }
                    }
                }
            } footer: {
                Text("A profile bundles format, scale, picture look and audio delay. Apply uses it on the card in the HDMI Captain window you used last.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                HStack {
                    Button("Save Current as Profile") {
                        if let session { app.saveProfile(from: session) }
                    }
                    .disabled(session == nil)
                    Spacer()
                    Button("Import…") { importProfiles() }
                    Button("Export…") { exportProfiles() }
                        .disabled(app.profileStore.userProfiles.isEmpty)
                }
                if let message {
                    Text(message).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 560, height: 620)
    }

    private func update(_ profile: Profile, _ change: (inout Profile) -> Void) {
        var updated = profile
        change(&updated)
        app.updateProfile(updated)
    }

    private func importProfiles() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            message = "Imported \(try app.importProfiles(from: url)) profile(s)."
        } catch {
            message = "Import failed: \(error.localizedDescription)"
        }
    }

    private func exportProfiles() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "HDMI Captain Profiles.json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try app.exportProfiles(to: url)
            message = "Exported to \(url.lastPathComponent)."
        } catch {
            message = "Export failed: \(error.localizedDescription)"
        }
    }
}
