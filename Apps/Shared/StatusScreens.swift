import CaptureCore
import SwiftUI

// Full-window state screens (everything that isn't the live picture). One layout for all of
// them — visual, title, one-line explanation, actions — so states read as one family.

/// The shared layout: 40 pt symbol (or an illustration / spinner), semibold title, short message.
struct StateCard<Visual: View, Actions: View>: View {
    let title: String
    let message: String
    var detail: String?
    @ViewBuilder let visual: Visual
    @ViewBuilder let actions: Actions

    var body: some View {
        VStack(spacing: 0) {
            visual
                .font(.system(size: 40, weight: .regular))
                .frame(minHeight: 48)
                .padding(.bottom, 16)
            Text(title)
                .font(.title2.weight(.semibold))
            Text(message)
                .font(.body)
                .foregroundStyle(.white.opacity(0.75))
                .padding(.top, 8)
            if let detail {
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.5))
                    .padding(.top, 6)
            }
            HStack(spacing: 10) { actions }
                .padding(.top, 18)
        }
        .frame(maxWidth: 440)
        .multilineTextAlignment(.center)
        .foregroundStyle(.white)
        .padding(32)
        .accessibilityElement(children: .contain)
    }
}

extension StateCard where Actions == EmptyView {
    init(title: String, message: String, detail: String? = nil, @ViewBuilder visual: () -> Visual) {
        self.init(title: title, message: message, detail: detail, visual: visual, actions: { EmptyView() })
    }
}

/// Covers the window when there is no picture worth keeping on screen.
struct StatusOverlay: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app
    @State private var slow = false

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(.black.opacity(0.7))
            // Connecting that drags on gets an explanation and a way out.
            .task(id: viewer.phase) {
                slow = false
                guard case .connecting = viewer.phase else { return }
                try? await Task.sleep(for: .seconds(8))
                if !Task.isCancelled { slow = true }
            }
    }

    @ViewBuilder private var content: some View {
        switch viewer.phase {
        case .permissionNeeded:
            // First run: explain before the system asks.
            StateCard(title: "Welcome to HDMI Captain",
                      message: "Your \(StatusCopy.device) sees an HDMI capture card as a camera and a microphone, so it will ask for access next. HDMI Captain uses it only to show the picture and play the sound, and nothing leaves your \(StatusCopy.device).") {
                ConnectIllustration()
            } actions: {
                Button("Continue") { app.continueFromWelcome() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            }
        case .permissionDenied:
            StateCard(title: "Camera access required", message: StatusCopy.permissionHelp) {
                Image(systemName: "video.slash")
            } actions: {
                Button("Open Settings") { app.openPrivacySettings() }.buttonStyle(.borderedProminent)
                Button("Check Again") { app.retry() }.buttonStyle(.bordered)
            }
        case .scanning:
            StateCard(title: "Connect a capture card",
                      message: "Plug an HDMI capture card into this \(StatusCopy.device). The picture appears automatically.") {
                ConnectIllustration()
            } actions: {
                Button("Connection Help…") { app.isShowingHelp = true }.buttonStyle(.bordered)
            }
        case let .waiting(name):
            StateCard(title: "\(name) is disconnected", message: "Plug it back in to continue where you left off.") {
                Image(systemName: "cable.connector.slash")
            }
        case let .connecting(name):
            StateCard(title: "Connecting to \(name)…",
                      message: slow ? "This is taking longer than usual." : "Starting the capture card.") {
                ProgressView().controlSize(.large).tint(.white)
            } actions: {
                if slow { Button("Reconnect") { app.retry() }.buttonStyle(.bordered) }
            }
        case let .recovering(reason):
            let copy = StatusCopy.recovering(reason)
            StateCard(title: copy.title, message: copy.message) {
                if copy.isNoSignal {
                    Image(systemName: "rectangle.slash")
                } else {
                    ProgressView().controlSize(.large).tint(.white)
                }
            }
        case let .paused(reason):
            StateCard(title: "Paused", message: reason.message, detail: reason.hint) {
                PauseIllustration(reason: reason)
            }
        case let .failed(message):
            StateCard(title: "The capture card stopped responding",
                      message: "Try reconnecting. If that doesn't help, unplug the card and plug it back in.",
                      detail: message) {
                Image(systemName: "exclamationmark.triangle")
            } actions: {
                Button("Reconnect") { app.retry() }.buttonStyle(.borderedProminent)
            }
        case .live:
            EmptyView()
        }
    }
}

/// The card is live but shows no picture (its own "no signal" screen, or black).
struct NoSignalOverlay: View {
    @Environment(ViewerModel.self) private var viewer
    @Environment(AppModel.self) private var app

    var body: some View {
        StateCard(title: "No HDMI signal", message: message) {
            Image(systemName: "rectangle.slash")
        } actions: {
            Button("Connection Help…") { app.isShowingHelp = true }.buttonStyle(.bordered)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.black.opacity(0.55))
    }

    private var message: String {
        switch viewer.signal {
        case .black:
            "The card receives a black picture. If the source is a PlayStation or Apple TV, turn off HDCP in its settings."
        default:
            "Check that the source is on and the HDMI cable is plugged in."
        }
    }
}

/// Words for states that come from the engine as free text.
enum StatusCopy {
    static var device: String {
        #if os(macOS)
        "Mac"
        #else
        "iPad"
        #endif
    }

    static var permissionHelp: String {
        #if os(macOS)
        "Turn on HDMI Captain in System Settings › Privacy & Security › Camera, then come back."
        #else
        "Turn on Camera for HDMI Captain in Settings, then come back."
        #endif
    }

    /// Title, message and whether the cause is the source (no spinner) rather than the card.
    static func recovering(_ reason: String) -> (title: String, message: String, isNoSignal: Bool) {
        switch reason {
        case "No signal":
            ("No HDMI signal", "Check that the source is on and the HDMI cable is plugged in.", true)
        case "Device disconnected", "Waiting for device":
            ("Capture card disconnected", "Plug it back in to continue.", false)
        default:
            ("Reconnecting…", "The capture card stopped sending video. Reconnecting automatically.", false)
        }
    }

    /// One-line banner over the last frame while recovering.
    static func recoveringBanner(_ reason: String) -> String {
        reason == "No signal" ? "No HDMI signal" : "Reconnecting…"
    }
}

/// Source → capture card → this device, with a gently pulsing signal.
struct ConnectIllustration: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "gamecontroller")
            link
            Image(systemName: "rectangle.connected.to.line.below")
            link
            #if os(macOS)
            Image(systemName: "laptopcomputer")
            #else
            Image(systemName: "ipad.landscape")
            #endif
        }
        .font(.system(size: 34, weight: .regular))
        .accessibilityHidden(true)
    }

    private var link: some View {
        Capsule()
            .fill(.white.opacity(0.6))
            .frame(width: 26, height: 3)
            .phaseAnimator(reduceMotion ? [1.0] : [0.35, 1.0]) { line, opacity in
                line.opacity(opacity)
            } animation: { _ in .easeInOut(duration: 0.9) }
    }
}
