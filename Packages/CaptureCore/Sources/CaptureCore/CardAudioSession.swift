#if os(iOS)
import AVFoundation
import os

/// iPadOS has a single audio input route. HDMI audio is only played when that route is the
/// capture card's USB audio; otherwise we'd be monitoring the iPad's own microphone.
public enum CardAudioSession {
    static let log = Logger(subsystem: "hdmicaptain", category: "audio")

    /// Sets up capture + playback and selects the card's USB input.
    /// Returns true when the card is the active input (safe to capture audio).
    public static func activate() -> Bool {
        let session = AVAudioSession.sharedInstance()
        do {
            // Speaker by default; AirPods/Bluetooth for output only (A2DP, never the HFP mic).
            try session.setCategory(.playAndRecord, mode: .default, options: [.defaultToSpeaker, .allowBluetoothA2DP])
            try session.setPreferredIOBufferDuration(0.005)
            try session.setActive(true)
        } catch {
            log.error("Audio session setup failed: \(error.localizedDescription, privacy: .public)")
            return false
        }
        guard let card = session.availableInputs?.first(where: { $0.portType == .usbAudio }) else {
            log.info("No USB audio input; HDMI audio off (inputs: \(describe(session.availableInputs ?? []), privacy: .public))")
            return false
        }
        do {
            try session.setPreferredInput(card)
        } catch {
            log.error("Could not select \(card.portName, privacy: .public): \(error.localizedDescription, privacy: .public)")
        }
        let route = session.currentRoute
        let active = route.inputs.contains { $0.portType == .usbAudio }
        log.info("Audio route: in \(describe(route.inputs), privacy: .public) → out \(describe(route.outputs), privacy: .public); card audio \(active ? "on" : "off", privacy: .public)")
        return active
    }

    private static func describe(_ ports: [AVAudioSessionPortDescription]) -> String {
        ports.map { "\($0.portName) (\($0.portType.rawValue))" }.joined(separator: ", ")
    }
}
#endif
