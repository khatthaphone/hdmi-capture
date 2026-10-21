import CaptureCore
import StoreKit
import SwiftUI

extension ProFeature {
    var symbol: String {
        switch self {
        case .recording: Symbol.record
        case .replayBuffer: Symbol.saveRecent
        case .streaming: Symbol.goLive
        case .virtualCamera: Symbol.virtualCamera
        }
    }

    var summary: String {
        switch self {
        case .recording: "Save full-quality video of the HDMI input, with its sound."
        case .replayBuffer: "Missed a great moment? Save the last 15 seconds to 60 minutes after it happened."
        case .streaming: "Go live on Twitch, YouTube or any RTMP/SRT server."
        case .virtualCamera: "Use the capture card as a camera in Zoom, Meet and FaceTime."
        }
    }

    /// Pro features that are built so far; the rest show "coming soon" once unlocked.
    static let available: Set<ProFeature> = [.recording, .replayBuffer, .streaming]

    /// Hidden from every menu, button and the upgrade sheet until they ship, so Pro never sells
    /// something that isn't there yet (ux-review #6). Streaming's code (Streaming/) is kept; remove
    /// a case here to bring it back.
    static let hidden: Set<ProFeature> = [.streaming, .virtualCamera]

    /// Pro features shown in the UI, in order.
    static var shown: [ProFeature] { allCases.filter { !hidden.contains($0) } }
}

/// One-time upgrade sheet. Never shown unless the user picks a Pro feature or opens it.
struct UpgradeView: View {
    @Environment(EntitlementStore.self) private var store
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            Image("AppLogo")
                .resizable()
                .frame(width: 88, height: 88)
                .overlay(alignment: .bottomTrailing) {
                    if store.isPro {
                        Image(systemName: "checkmark.seal.fill")
                            .font(.system(size: 26))
                            .foregroundStyle(.white, .green)
                    }
                }
                .padding(.bottom, 12)
                .accessibilityHidden(true)
            Text(title)
                .font(.title.bold())
                .multilineTextAlignment(.center)
            Text(store.isPro ? "Thank you for supporting HDMI Captain." : "One-time purchase. No subscription.")
                .font(.headline)
                .foregroundStyle(.secondary)
                .padding(.top, 6)

            VStack(alignment: .leading, spacing: 14) {
                ForEach(ProFeature.shown, id: \.self) { feature in
                    HStack(alignment: .top, spacing: 12) {
                        Image(systemName: feature.symbol)
                            .font(.title2)
                            .frame(width: 32)
                            .foregroundStyle(feature == store.highlightedFeature ? Color.accentColor : .primary)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(feature.title).font(.headline)
                            Text(feature.summary).font(.callout).foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                }
            }
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
            .padding(.top, 20)

            Text("The live viewer, screenshots and every picture setting stay free.")
                .font(.footnote)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.top, 12)

            if case let .failed(message) = store.purchaseState {
                Text(message).foregroundStyle(.red).font(.callout).padding(.top, 10)
            } else if store.purchaseState == .pending {
                Text("Waiting for approval. Pro unlocks as soon as the purchase is approved.")
                    .foregroundStyle(.secondary).font(.callout).padding(.top, 10)
            }

            VStack(spacing: 10) {
                if store.isPro {
                    Button("Done") { dismiss() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                } else {
                    Button {
                        Task { await store.purchase() }
                    } label: {
                        Group {
                            if store.purchaseState == .purchasing {
                                ProgressView().controlSize(.small)
                            } else {
                                Text(store.product.map { "Buy Pro — \($0.displayPrice)" } ?? "Buy Pro")
                            }
                        }
                        .frame(maxWidth: .infinity)
                    }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(store.purchaseState == .purchasing)
                    Button("Not Now") { dismiss() }
                        .keyboardShortcut(.cancelAction)
                        .buttonStyle(.bordered)
                        .controlSize(.large)
                }
                Button("Restore Purchase") { Task { await store.restore() } }
                    .buttonStyle(.borderless)
                    .font(.callout)
            }
            .padding(.top, 20)
        }
        .padding(28)
        .frame(width: 440)
        .task { if store.product == nil { await store.loadProduct() } }
    }

    /// Names what the user was trying to do when the sheet opened from a locked feature.
    private var title: String {
        if store.isPro { return "You have HDMI Captain Pro" }
        switch store.highlightedFeature {
        case .recording: return "Record with HDMI Captain Pro"
        case .replayBuffer: return "Save Recent with HDMI Captain Pro"
        default: return "HDMI Captain Pro"
        }
    }
}

/// Settings → Pro tab.
struct ProSettingsView: View {
    var body: some View {
        Form {
            ProSettingsSections()
        }
        .formStyle(.grouped)
        .frame(width: 560, height: 620)
    }
}

/// The Pro sections; the Mac's Settings → Pro tab and the iPad Settings sheet.
struct ProSettingsSections: View {
    @Environment(EntitlementStore.self) private var store
    @Environment(AppModel.self) private var app

    /// Disk space the buffer uses at the current card's mode (≈ 0.10 bit per pixel per frame).
    private var diskEstimate: String {
        let info = app.currentViewer?.liveInfo
        let width = Double(info?.format.width ?? 1920), height = Double(info?.format.height ?? 1080)
        let fps = info?.frameRate ?? 60
        let bytes = width * height * fps * 0.10 / 8 * Double(app.prefs.recentMinutes * 60)
        let size = ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file)
        return "Uses about \(size) of storage while on, at \(Int(width))×\(Int(height)) \(Int(fps.rounded())) fps. Nothing is kept unless you choose Save Recent."
    }

    var body: some View {
        @Bindable var store = store
        @Bindable var app = app
        Group {
            Section("Pro") {
                LabeledContent("Status", value: store.isPro ? "Pro unlocked" : "Free")
                if !store.isPro {
                    Button("Upgrade to Pro…") { store.requestUpgrade() }
                }
                Button("Restore Purchase") { Task { await store.restore() } }
            }
            Section("Included in Pro") {
                ForEach(ProFeature.shown, id: \.self) { feature in
                    Label(feature.title, systemImage: feature.symbol)
                }
            }
            Section {
                Toggle("Keep recent footage", isOn: Binding(
                    get: { app.prefs.recentEnabled && store.isUnlocked(.replayBuffer) },
                    set: { on in if !on || store.gate(.replayBuffer) { app.prefs.recentEnabled = on } }
                ))
                Picker("Keep up to", selection: $app.prefs.recentMinutes) {
                    ForEach([1, 5, 15, 30, 60], id: \.self) { Text($0 == 1 ? "1 minute" : "\($0) minutes").tag($0) }
                }
                Text(diskEstimate)
                    .font(.caption).foregroundStyle(.secondary)
            } header: {
                ProSectionHeader(title: "Save Recent", locked: !store.isUnlocked(.replayBuffer))
            }
            .disabled(!store.isUnlocked(.replayBuffer))
            Section {
                Picker("Codec", selection: $app.prefs.recordingCodec) {
                    ForEach(Recorder.Codec.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                Toggle("Show recording timer", isOn: $app.prefs.showRecordingIndicator)
                #if os(macOS)
                Button("Show Recordings in Finder") { app.showRecordingsInFinder() }
                #endif
            } header: {
                ProSectionHeader(title: "Recording", locked: !store.isUnlocked(.recording))
            }
            .disabled(!store.isUnlocked(.recording))
            #if DEBUG
            Section("Developer") {
                Toggle("Unlock Pro (debug builds only)", isOn: $store.debugUnlock)
            }
            #endif
        }
    }
}

/// "Save Recent  🔒 Requires Pro" when locked.
private struct ProSectionHeader: View {
    let title: String
    let locked: Bool

    var body: some View {
        HStack {
            Text(title)
            if locked {
                Spacer()
                Label("Requires Pro", systemImage: Symbol.locked).labelStyle(.titleAndIcon)
            }
        }
    }
}
