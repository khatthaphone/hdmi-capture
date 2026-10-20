import Atomics
import Foundation

/// Single-producer / single-consumer ring of interleaved Float frames.
/// Both sides are lock- and allocation-free, so the consumer can run on the real-time audio thread.
public final class AudioRingBuffer: @unchecked Sendable {
    public let capacity: Int
    public let channels: Int
    private let mask: Int
    private let storage: UnsafeMutablePointer<Float>
    private let written = ManagedAtomic<Int>(0)
    private let consumed = ManagedAtomic<Int>(0)

    /// `capacityFrames` is rounded up to a power of two.
    public init(capacityFrames: Int, channels: Int) {
        var size = 1
        while size < capacityFrames { size <<= 1 }
        capacity = size
        mask = size - 1
        self.channels = channels
        storage = .allocate(capacity: size * channels)
        storage.initialize(repeating: 0, count: size * channels)
    }

    deinit {
        storage.deallocate()
    }

    public var availableFrames: Int {
        written.load(ordering: .acquiring) - consumed.load(ordering: .acquiring)
    }

    /// Producer side. Frames that don't fit are dropped; returns how many were stored.
    @discardableResult
    public func write(_ source: UnsafePointer<Float>, frames: Int) -> Int {
        let w = written.load(ordering: .relaxed)
        let r = consumed.load(ordering: .acquiring)
        let count = min(frames, capacity - (w - r))
        guard count > 0 else { return 0 }
        let start = w & mask
        let first = min(count, capacity - start)
        (storage + start * channels).update(from: source, count: first * channels)
        if count > first {
            storage.update(from: source + first * channels, count: (count - first) * channels)
        }
        written.store(w + count, ordering: .releasing)
        return count
    }

    /// Consumer side. Copies up to `frames` interleaved frames into `destination`.
    @discardableResult
    public func read(into destination: UnsafeMutablePointer<Float>, frames: Int) -> Int {
        let r = consumed.load(ordering: .relaxed)
        let w = written.load(ordering: .acquiring)
        let count = min(frames, w - r)
        guard count > 0 else { return 0 }
        let start = r & mask
        let first = min(count, capacity - start)
        destination.update(from: storage + start * channels, count: first * channels)
        if count > first {
            (destination + first * channels).update(from: storage, count: (count - first) * channels)
        }
        consumed.store(r + count, ordering: .releasing)
        return count
    }

    /// Consumer side. Drops up to `frames` frames without copying.
    @discardableResult
    public func discard(_ frames: Int) -> Int {
        let r = consumed.load(ordering: .relaxed)
        let w = written.load(ordering: .acquiring)
        let count = min(frames, w - r)
        guard count > 0 else { return 0 }
        consumed.store(r + count, ordering: .releasing)
        return count
    }
}

/// Keeps the buffered audio near a target (the user's delay plus a jitter margin).
/// Small clock drift between the capture card and the output device is absorbed by consuming
/// one frame more or less per callback; large changes (delay edits) jump or re-prime.
public struct DriftController: Sendable {
    public enum Plan: Equatable, Sendable {
        /// Output silence (priming or underrun).
        case silence
        /// Drop `discard` frames, then stretch `consume` frames to fill the request.
        case play(discard: Int, consume: Int)
    }

    public var targetFrames: Int
    public let toleranceFrames: Int
    public let jumpFrames: Int
    public private(set) var priming = true
    public private(set) var averageFill: Double = 0

    /// Smoothing for the fill average; the sawtooth from chunked capture delivery must not trigger corrections.
    private let smoothing = 0.02
    /// Proportional gain: frames of correction per callback per frame of error.
    private let gain = 0.01
    /// Fractional correction carried between callbacks.
    private var carry = 0.0

    /// Buffered frames needed so chunked capture delivery never starves the output:
    /// half a capture chunk (sawtooth) + one render quantum + tolerance + 5 ms safety.
    public static func target(delayFrames: Int, chunkFrames: Int, quantumFrames: Int, toleranceFrames: Int = 240) -> Int {
        delayFrames + chunkFrames / 2 + quantumFrames + toleranceFrames + 240
    }

    public init(targetFrames: Int, toleranceFrames: Int = 240, jumpFrames: Int = 4800) {
        self.targetFrames = targetFrames
        self.toleranceFrames = toleranceFrames
        self.jumpFrames = jumpFrames
    }

    public mutating func plan(available: Int, request: Int) -> Plan {
        if priming {
            guard available >= max(targetFrames, request) else { return .silence }
            priming = false
            averageFill = Double(available)
            carry = 0
        }
        averageFill += (Double(available) - averageFill) * smoothing

        var discard = 0
        let error = averageFill - Double(targetFrames)
        if error > Double(jumpFrames) {
            discard = max(0, min(available - request, Int(error)))
            averageFill -= Double(discard)
        } else if error < -Double(jumpFrames) {
            priming = true
            return .silence
        }

        // Proportional speed correction with fractional carry: at 0.1 % clock skew the
        // steady-state error is ~1 ms, and the speed change never exceeds 3 frames per callback.
        carry += max(-3, min(3, (averageFill - Double(targetFrames)) * gain))
        let whole = Int(carry)
        carry -= Double(whole)
        let consume = max(1, request + whole)
        guard available - discard >= consume else {
            priming = true
            return .silence
        }
        return .play(discard: discard, consume: consume)
    }
}

/// How the two captured channels map to the output.
public enum ChannelMode: String, Codable, CaseIterable, Sendable {
    /// Normal for stereo sources; left to both for mono sources.
    case auto
    case normal
    case swap
    case leftToBoth
    case rightToBoth
    case mono

    public func resolved(sourceChannels: Int) -> ChannelMode {
        self == .auto ? (sourceChannels == 1 ? .leftToBoth : .normal) : self
    }

    /// Applies the mapping in place to interleaved stereo frames.
    public func apply(to samples: UnsafeMutablePointer<Float>, frames: Int) {
        for i in 0..<frames {
            let l = samples[2 * i], r = samples[2 * i + 1]
            switch self {
            case .auto, .normal: continue
            case .swap: (samples[2 * i], samples[2 * i + 1]) = (r, l)
            case .leftToBoth: samples[2 * i + 1] = l
            case .rightToBoth: samples[2 * i] = r
            case .mono: let m = (l + r) * 0.5; (samples[2 * i], samples[2 * i + 1]) = (m, m)
            }
        }
    }
}
