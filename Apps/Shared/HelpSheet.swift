import SwiftUI

/// Connection Help: the problems people actually hit with capture cards, each with steps.
/// Opened from the empty and no-signal screens, the ⋯ menu and Help › HDMI Captain Help.
struct HelpSheet: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                topic("No HDMI signal", symbol: "rectangle.slash", steps: [
                    "Turn the source on and make sure it isn't asleep.",
                    "Unplug the HDMI cable at both ends and plug it back in.",
                    "Pick the source's HDMI output (not its built-in screen) and a standard resolution such as 1080p.",
                ])
                topic("Black picture from a PlayStation or Apple TV", symbol: "lock.rectangle", steps: [
                    "These sources protect video with HDCP, and capture cards can't show protected video.",
                    "PS5: Settings › System › HDMI › turn off Enable HDCP. PS4: Settings › System › turn off Enable HDCP.",
                    "Apple TV and streaming apps always use HDCP for movies and TV; games and menus usually work.",
                ])
                topic("No sound", symbol: "speaker.slash", steps: soundSteps)
                topic("Capture card not found", symbol: "cable.connector.slash", steps: notFoundSteps)
                topic("Picture lags or stutters", symbol: "gauge.with.dots.needle.33percent", steps: [
                    "Plug the card straight into the \(StatusCopy.device), not through a hub, when you can.",
                    "Two cards on one USB 2.0 bus share bandwidth; use separate ports or lower the format in Settings.",
                    "Turn on Show Diagnostics to see frame rate, dropped frames and latency.",
                ])
            }
            .navigationTitle("Connection Help")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
        #if os(macOS)
        .frame(width: 520, height: 560)
        #endif
    }

    private var soundSteps: [String] {
        #if os(macOS)
        [
            "Check that the source isn't muted and that sound is on in HDMI Captain (speaker button on the bar).",
            "Allow HDMI Captain in System Settings › Privacy & Security › Microphone; capture cards deliver sound as a microphone.",
            "Choose the audio output in Settings › Sound.",
        ]
        #else
        [
            "Check that the source isn't muted and that sound is on in HDMI Captain (speaker button on the bar).",
            "Allow Microphone for HDMI Captain in Settings; capture cards deliver sound as a microphone.",
            "The card must be the iPad's audio input: unplug other USB microphones or headsets, then unplug and replug the card.",
        ]
        #endif
    }

    private var notFoundSteps: [String] {
        #if os(macOS)
        [
            "Use a UVC capture card (most USB HDMI capture cards are). Cards that need a driver won't appear.",
            "Try another USB port or cable; some cables only charge.",
            "Allow HDMI Captain in System Settings › Privacy & Security › Camera.",
        ]
        #else
        [
            "Capture cards work on iPads with USB-C (iPadOS 17 or later). iPhone doesn't support them.",
            "Use a UVC capture card (most USB HDMI capture cards are). Some need more power than the iPad gives; use a powered USB-C hub.",
            "Allow Camera for HDMI Captain in Settings.",
        ]
        #endif
    }

    private func topic(_ title: String, symbol: String, steps: [String]) -> some View {
        Section {
            ForEach(Array(steps.enumerated()), id: \.element) { index, step in
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Text("\(index + 1).").monospacedDigit().foregroundStyle(.secondary)
                    Text(step).fixedSize(horizontal: false, vertical: true)
                }
            }
        } header: {
            Label(title, systemImage: symbol)
        }
    }
}
