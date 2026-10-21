import AppKit
import CaptureCore
import QuartzCore
import SwiftUI
import UniformTypeIdentifiers

/// Built-in latency test (task 035): the Mac's own HDMI output loops into the capture card; a
/// full-screen window on that display flips black/white and LatencyProbe times each flip until
/// the card delivers it. The window is the only thing drawn there; it is never part of outputs.
@MainActor @Observable
final class LatencyTestRun {
    enum Phase: Equatable {
        case ready
        case running(done: Int)
        case finished(LatencyProbe.Result, onScreenMs: Double?)
    }

    static let flipCount = 30

    private(set) var phase: Phase = .ready

    /// Probe shared with the capture queue.
    private final class Box: @unchecked Sendable {
        let lock = NSLock()
        var probe = LatencyProbe()
    }

    private var box = Box()
    private var window: NSWindow?
    private var link: CADisplayLink?
    private var sink: LumaProbeSink?
    private weak var engine: CaptureEngine?
    private weak var renderer: MetalRenderer?
    private var white = false
    private var flips = 0
    private var nextFlip = 0.0

    /// Displays other than the one this window is on: where the loop cable would go.
    static var candidateScreens: [NSScreen] { NSScreen.screens.count > 1 ? Array(NSScreen.screens.dropFirst()) : [] }

    func start(on screen: NSScreen, engine: CaptureEngine, renderer: MetalRenderer) {
        stop()
        box = Box()
        flips = 0
        white = false
        self.engine = engine
        self.renderer = renderer
        let box = self.box
        let sink = LumaProbeSink { time, meanY in
            box.lock.withLock { box.probe.frame(at: time, meanY: meanY) }
        }
        engine.addSink(sink)
        self.sink = sink

        let window = NSWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false, screen: screen)
        window.level = .screenSaver
        window.isReleasedWhenClosed = false
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let view = NSView(frame: NSRect(origin: .zero, size: screen.frame.size))
        view.wantsLayer = true
        view.layer?.backgroundColor = NSColor.black.cgColor
        window.contentView = view
        window.setFrame(screen.frame, display: true)
        window.orderFrontRegardless()
        self.window = window

        // Flip on display-link ticks so each flip's time is the refresh it appears on.
        let link = view.displayLink(target: self, selector: #selector(tick(_:)))
        link.add(to: .main, forMode: .common)
        self.link = link
        nextFlip = CACurrentMediaTime() + 1  // let the card see black first
        phase = .running(done: 0)
    }

    @objc private func tick(_ link: CADisplayLink) {
        guard case .running = phase, CACurrentMediaTime() >= nextFlip else { return }
        if flips >= Self.flipCount {
            finish()
            return
        }
        white.toggle()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        window?.contentView?.layer?.backgroundColor = (white ? NSColor.white : NSColor.black).cgColor
        CATransaction.commit()
        let shownAt = link.targetTimestamp
        let isWhite = white
        box.lock.withLock { box.probe.flip(at: shownAt, white: isWhite) }
        flips += 1
        phase = .running(done: flips)
        // Irregular spacing so the flips never lock to the card's frame rate.
        nextFlip = CACurrentMediaTime() + 0.45 + Double.random(in: 0...0.2)
    }

    private func finish() {
        let result = box.lock.withLock { box.probe.result }
        // Arrival → on this Mac's screen, from the renderer's own measurements.
        let onScreen = renderer.flatMap { r -> Double? in
            let l = r.latency()
            guard let capture = l.capture, let display = l.display else { return nil }
            return max(0, display.averageMs - capture.averageMs)
        }
        stop()
        phase = .finished(result, onScreenMs: onScreen)
    }

    func stop() {
        link?.invalidate()
        link = nil
        window?.orderOut(nil)
        window = nil
        if let sink { engine?.removeSink(sink) }
        sink = nil
    }

    func reset() {
        stop()
        phase = .ready
    }
}

/// Video › Latency Test…
struct LatencyTestSheet: View {
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var run = LatencyTestRun()
    @State private var screenIndex = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Latency Test").font(.title2.bold())
            switch run.phase {
            case .ready: ready
            case let .running(done):
                ProgressView(value: Double(done), total: Double(LatencyTestRun.flipCount)) {
                    Text("Measuring… keep the cable connected (\(done) of \(LatencyTestRun.flipCount))")
                }
            case let .finished(result, onScreen): results(result, onScreen: onScreen)
            }
            HStack {
                Spacer()
                if case .running = run.phase {
                    Button("Stop") { run.reset() }
                }
                Button("Done") { run.stop(); dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 480)
        .onDisappear { run.stop() }
    }

    @ViewBuilder private var ready: some View {
        Text("Connect this Mac's HDMI output (or a USB-C to HDMI adapter) to the capture card's HDMI input. HDMI Captain flips that display black and white and times how long each flip takes to come back through the card.")
            .fixedSize(horizontal: false, vertical: true)
        let screens = LatencyTestRun.candidateScreens
        if screens.isEmpty {
            Label("No second display found. Connect the cable, then try again.", systemImage: "display.trianglebadge.exclamationmark")
                .foregroundStyle(.secondary)
        } else {
            Picker("Display", selection: $screenIndex) {
                ForEach(Array(screens.enumerated()), id: \.offset) { index, screen in
                    Text(screen.localizedName).tag(index)
                }
            }
            Button("Start Test") {
                guard let viewer = app.currentViewer, let session = viewer.session, session.state == .live, let renderer = viewer.renderer else {
                    app.currentViewer?.showToast("Connect a capture card to run the latency test", .warning)
                    return
                }
                run.start(on: screens[min(screenIndex, screens.count - 1)], engine: session.engine, renderer: renderer)
            }
            .buttonStyle(.borderedProminent)
            Text("Takes about 20 seconds. The display shows a full-screen flashing pattern while it runs.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private func results(_ result: LatencyProbe.Result, onScreen: Double?) -> some View {
        if let median = result.medianMs, let p95 = result.p95Ms {
            Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 6) {
                GridRow { Text("Through the card").foregroundStyle(.secondary); Text(ms(median)).monospacedDigit(); Text("p95 \(ms(p95))").foregroundStyle(.secondary) }
                if let onScreen {
                    GridRow { Text("On this screen").foregroundStyle(.secondary); Text("+ \(ms(onScreen))").monospacedDigit(); Text("") }
                    GridRow { Text("Glass to glass").bold(); Text(ms(median + onScreen)).bold().monospacedDigit(); Text("± one refresh").foregroundStyle(.secondary) }
                }
            }
            Text("\(result.samplesMs.count) of \(result.flips) flips measured\(result.missed > 0 ? "; \(result.missed) not seen" : "").")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("Run Again") { run.reset() }
                Button("Save Report…") { save(result, onScreen: onScreen) }
            }
        } else {
            Label("No flips came back through the capture card. Check that the cable goes from this Mac's HDMI output to the card's input, and that the right display is selected.",
                  systemImage: "exclamationmark.triangle")
                .fixedSize(horizontal: false, vertical: true)
            Button("Try Again") { run.reset() }
        }
    }

    private func ms(_ value: Double) -> String { String(format: "%.1f ms", value) }

    /// JSON + Markdown, for docs/research/latency/ (task 035).
    private func save(_ result: LatencyProbe.Result, onScreen: Double?) {
        let card = app.currentViewer?.session?.deviceName ?? "Unknown card"
        let mode = app.currentViewer?.session?.liveInfo.map { "\($0.format.width)x\($0.format.height) @ \(Int($0.frameRate.rounded()))" } ?? ""
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "Latency \(card).md"
        panel.allowedContentTypes = [UTType(filenameExtension: "md") ?? .plainText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let json: [String: Any] = [
            "card": card, "mode": mode, "date": ISO8601DateFormatter().string(from: Date()),
            "flips": result.flips, "samplesMs": result.samplesMs,
            "medianMs": result.medianMs ?? 0, "p95Ms": result.p95Ms ?? 0, "onScreenMs": onScreen ?? NSNull(),
        ]
        let glass = result.medianMs.flatMap { m in onScreen.map { ms(m + $0) } } ?? "–"
        let markdown = """
        # Latency — \(card)

        - Mode: \(mode)
        - Through the card: median \(result.medianMs.map(ms) ?? "–"), p95 \(result.p95Ms.map(ms) ?? "–") (\(result.samplesMs.count)/\(result.flips) flips)
        - On this screen: \(onScreen.map(ms) ?? "–")
        - Glass to glass: \(glass) ± one refresh

        """
        try? markdown.write(to: url, atomically: true, encoding: .utf8)
        if let data = try? JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: url.deletingPathExtension().appendingPathExtension("json"))
        }
    }
}
