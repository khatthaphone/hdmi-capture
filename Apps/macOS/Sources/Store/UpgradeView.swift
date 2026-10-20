import CaptureCore
import StoreKit
import SwiftUI

extension ProFeature {
    var symbol: String {
        switch self {
        case .recording: "record.circle"
        case .replayBuffer: "gobackward.30"
        case .streaming: "dot.radiowaves.left.and.right"
        case .virtualCamera: "web.camera"
        }
    }

    var summary: String {
        switch self {
        case .recording: "Record the HDMI input to HEVC or H.264 with audio."
        case .replayBuffer: "Keep the last moments in memory and save them with one key."
        case .streaming: "Go live on Twitch, YouTube or any RTMP/SRT server."
        case .virtualCamera: "Use the capture card as a camera in Zoom, Meet and FaceTime."
        }
    }

    /// Pro features that are built so far; the rest show "coming soon" once unlocked.
    static let available: Set<ProFeature> = []
}

/// One-time upgrade sheet. Never shown unless the user picks a Pro feature or opens it.
struct UpgradeView: View {
    @Environment(EntitlementStore.self) private var store
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 18) {
            Image(systemName: store.isPro ? "checkmark.seal.fill" : "sparkles")
                .font(.system(size: 44))
                .foregroundStyle(store.isPro ? .green : .accentColor)
            Text(store.isPro ? "You have HDMI Captain Pro" : "HDMI Captain Pro")
                .font(.title.bold())
            Text(store.isPro ? "Thank you for supporting HDMI Captain." : "One purchase, yours forever. The viewer stays free.")
                .foregroundStyle(.secondary)

            VStack(alignment: .leading, spacing: 12) {
                ForEach(ProFeature.allCases, id: \.self) { feature in
                    HStack(alignment: .top, spacing: 12) {
                        Image(systemName: feature.symbol)
                            .font(.title3)
                            .frame(width: 28)
                            .foregroundStyle(feature == store.highlightedFeature ? Color.accentColor : .primary)
                        VStack(alignment: .leading, spacing: 2) {
                            HStack {
                                Text(feature.title).font(.headline)
                                if !ProFeature.available.contains(feature) {
                                    Text("Coming soon").font(.caption).foregroundStyle(.secondary)
                                }
                            }
                            Text(feature.summary).font(.callout).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .padding()
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))

            if case let .failed(message) = store.purchaseState {
                Text(message).foregroundStyle(.red).font(.callout)
            } else if store.purchaseState == .pending {
                Text("Waiting for approval…").foregroundStyle(.secondary).font(.callout)
            }

            HStack {
                Button("Restore Purchases") { Task { await store.restore() } }
                Spacer()
                if store.isPro {
                    Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
                } else {
                    Button("Not Now") { dismiss() }.keyboardShortcut(.cancelAction)
                    Button {
                        Task { await store.purchase() }
                    } label: {
                        if store.purchaseState == .purchasing {
                            ProgressView().controlSize(.small)
                        } else {
                            Text(store.product.map { "Unlock for \($0.displayPrice)" } ?? "Unlock Pro")
                        }
                    }
                    .keyboardShortcut(.defaultAction)
                    .disabled(store.purchaseState == .purchasing)
                }
            }
        }
        .padding(28)
        .frame(width: 460)
        .task { if store.product == nil { await store.loadProduct() } }
    }
}

/// Settings → Pro tab.
struct ProSettingsView: View {
    @Environment(EntitlementStore.self) private var store

    var body: some View {
        @Bindable var store = store
        Form {
            Section {
                LabeledContent("Status", value: store.isPro ? "Pro unlocked" : "Free")
                if !store.isPro {
                    Button("Upgrade to Pro…") { store.requestUpgrade() }
                }
                Button("Restore Purchases") { Task { await store.restore() } }
            }
            Section("Included in Pro") {
                ForEach(ProFeature.allCases, id: \.self) { feature in
                    Label(feature.title, systemImage: feature.symbol)
                }
            }
            #if DEBUG
            Section("Developer") {
                Toggle("Unlock Pro (debug builds only)", isOn: $store.debugUnlock)
            }
            #endif
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
    }
}
