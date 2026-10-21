#if DEBUG
import CaptureCore
import Foundation
import QuartzCore

/// QA: `-qaSoakMinutes 30` appends one line a minute to `Documents/qa-soak.csv` (fps, drops,
/// memory, thermal state, audio buffer) and writes the card's formats to `qa-formats.txt`.
/// Fetch with `make ipad-soak` (iPad) or from the app container's Documents (Mac).
@MainActor
final class QASoak {
    private weak var app: AppModel?
    private let minutes: Int
    private var timer: Timer?
    private var started = CACurrentMediaTime()
    private var last: (stats: CaptureStats, time: CFTimeInterval)?
    private var samples = 0
    private let csv: URL
    private let formats: URL

    init(app: AppModel, minutes: Int) {
        self.app = app
        self.minutes = minutes
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        csv = documents.appendingPathComponent("qa-soak.csv")
        formats = documents.appendingPathComponent("qa-formats.txt")
        try? FileManager.default.createDirectory(at: documents, withIntermediateDirectories: true)
        try? "minute,state,device,mode,in_fps,dropped_total,dropped_per_min,memory_mb,thermal,audio_buffered_ms\n"
            .write(to: csv, atomically: true, encoding: .utf8)
    }

    func start() {
        started = CACurrentMediaTime()
        let timer = Timer(timeInterval: 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sample() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        // A first line shortly after the card is live, so the baseline is visible.
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(10))
            self?.sample()
        }
    }

    private func sample() {
        guard let app, let session = app.currentViewer?.session else { return }
        if samples == 0 { writeFormats(session) }
        let now = CACurrentMediaTime()
        let stats = session.currentStats()
        var fps = 0.0, droppedPerMinute: UInt64 = 0
        if let last, now > last.time {
            fps = Double(stats.framesReceived &- last.stats.framesReceived) / (now - last.time)
            droppedPerMinute = stats.framesDropped &- last.stats.framesDropped
        }
        last = (stats, now)
        let state = switch session.state {
        case .live: "live"
        case .connecting: "connecting"
        case .recovering: "recovering"
        case .paused: "paused"
        case .failed: "failed"
        case .absent: "absent"
        case .idle: "idle"
        }
        let mode = session.liveInfo.map { "\($0.format.width)x\($0.format.height)@\(Int($0.frameRate.rounded()))" } ?? "-"
        let thermal = switch ProcessInfo.processInfo.thermalState {
        case .nominal: "nominal"
        case .fair: "fair"
        case .serious: "serious"
        case .critical: "critical"
        @unknown default: "unknown"
        }
        let line = [
            String(format: "%.1f", (now - started) / 60), state, session.deviceName, mode,
            String(format: "%.1f", fps), String(stats.framesDropped), String(droppedPerMinute),
            String(format: "%.1f", Self.memoryMB()), thermal,
            String(format: "%.0f", session.audioLevels().bufferedMs),
        ].joined(separator: ",") + "\n"
        if let handle = try? FileHandle(forWritingTo: csv) {
            handle.seekToEndOfFile()
            handle.write(Data(line.utf8))
            try? handle.close()
        }
        samples += 1
        if Double(now - started) / 60 >= Double(minutes) {
            timer?.invalidate()
            timer = nil
            app.currentViewer?.showToast("Soak test finished", .info)
        }
    }

    private func writeFormats(_ session: DeviceSession) {
        let lines = session.formats.map { format in
            let rates = format.frameRateRanges.map { String(format: "%.2f", $0.max) }.joined(separator: "/")
            return "\(format.width)x\(format.height) \(format.fourCC) @ \(rates)"
        }
        try? (["\(session.deviceName)"] + lines).joined(separator: "\n").write(to: formats, atomically: true, encoding: .utf8)
    }

    /// Physical footprint, the number Xcode's memory gauge and the OS's limits use.
    private static func memoryMB() -> Double {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        return result == KERN_SUCCESS ? Double(info.phys_footprint) / 1_048_576 : 0
    }
}

/// QA: every engine state change with a timestamp, in `Documents/qa-events.csv`, so resume time
/// after an iPadOS interruption (paused → connecting → live) can be read off the device.
enum QAEvents {
    private static let url = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("qa-events.csv")
    private static var last: (state: String, time: CFTimeInterval)?

    static func record(_ state: CaptureState) {
        let name = switch state {
        case .idle: "idle"
        case .connecting: "connecting"
        case .live: "live"
        case let .recovering(reason): "recovering(\(reason))"
        case let .paused(reason): "paused(\(reason.rawValue))"
        case .failed: "failed"
        }
        let now = CACurrentMediaTime()
        let sincePrevious = last.map { String(format: "%.0f", (now - $0.time) * 1000) } ?? ""
        let line = "\(ISO8601DateFormatter().string(from: Date())),\(name),\(last?.state ?? ""),\(sincePrevious)\n"
        last = (name, now)
        if !FileManager.default.fileExists(atPath: url.path) {
            try? "time,state,previous,ms_since_previous\n".write(to: url, atomically: true, encoding: .utf8)
        }
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile()
            handle.write(Data(line.utf8))
            try? handle.close()
        }
    }
}
#endif
