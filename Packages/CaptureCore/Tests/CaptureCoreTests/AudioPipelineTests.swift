import Testing
@testable import CaptureCore

struct AudioRingBufferTests {
    @Test func writeReadWrapsAround() {
        let ring = AudioRingBuffer(capacityFrames: 8, channels: 2)
        #expect(ring.capacity == 8)
        var scratch = [Float](repeating: 0, count: 16)
        for round in 0..<5 {
            let input = (0..<12).map { Float(round * 100 + $0) } // 6 frames
            #expect(ring.write(input, frames: 6) == 6)
            #expect(ring.availableFrames == 6)
            #expect(ring.read(into: &scratch, frames: 6) == 6)
            #expect(Array(scratch[0..<12]) == input)
        }
    }

    @Test func dropsWhenFullAndReadsWhatIsThere() {
        let ring = AudioRingBuffer(capacityFrames: 4, channels: 2)
        let input = [Float](repeating: 1, count: 12)
        #expect(ring.write(input, frames: 6) == 4)
        var out = [Float](repeating: 0, count: 12)
        #expect(ring.read(into: &out, frames: 6) == 4)
        #expect(ring.read(into: &out, frames: 1) == 0)
    }

    @Test func discardSkipsFrames() {
        let ring = AudioRingBuffer(capacityFrames: 8, channels: 2)
        ring.write([1, 1, 2, 2, 3, 3], frames: 3)
        #expect(ring.discard(2) == 2)
        var out: [Float] = [0, 0]
        ring.read(into: &out, frames: 1)
        #expect(out == [3, 3])
    }
}

struct ChannelModeTests {
    private func run(_ mode: ChannelMode) -> [Float] {
        var samples: [Float] = [1, 0, 0.5, 0.25] // two frames: (L1,R0), (L.5,R.25)
        mode.apply(to: &samples, frames: 2)
        return samples
    }

    @Test func mappings() {
        #expect(run(.normal) == [1, 0, 0.5, 0.25])
        #expect(run(.swap) == [0, 1, 0.25, 0.5])
        #expect(run(.leftToBoth) == [1, 1, 0.5, 0.5])
        #expect(run(.rightToBoth) == [0, 0, 0.25, 0.25])
        #expect(run(.mono) == [0.5, 0.5, 0.375, 0.375])
    }

    @Test func autoResolvesBySourceChannels() {
        #expect(ChannelMode.auto.resolved(sourceChannels: 1) == .leftToBoth)
        #expect(ChannelMode.auto.resolved(sourceChannels: 2) == .normal)
        #expect(ChannelMode.swap.resolved(sourceChannels: 1) == .swap)
    }
}

struct StretchTests {
    @Test func identityCopiesPlanar() {
        let source: [Float] = [1, 2, 3, 4, 5, 6]
        var left = [Float](repeating: 0, count: 3), right = left
        AudioPipeline.stretch(source, frames: 3, toLeft: &left, right: &right, frames: 3)
        #expect(left == [1, 3, 5])
        #expect(right == [2, 4, 6])
    }

    @Test func oneExtraFrameInterpolatesEndpoints() {
        let source: [Float] = [0, 0, 1, 1, 2, 2, 3, 3, 4, 4] // 5 frames → 4
        var left = [Float](repeating: 0, count: 4), right = left
        AudioPipeline.stretch(source, frames: 5, toLeft: &left, right: &right, frames: 4)
        #expect(left.first == 0 && left.last == 4)
        #expect(zip(left, left.dropFirst()).allSatisfy { $0 < $1 })
    }
}

/// Simulates a capture card delivering 1024-frame chunks and an output pulling 512-frame
/// quanta, with the two clocks running at slightly different rates.
struct DriftControllerTests {
    struct Result {
        var underrunsAfterStart = 0
        var maxError = 0.0
        var finalFill = 0.0
    }

    private func simulate(seconds: Double, skew: Double, delayFrames: Int, changeDelayAt: (Double, Int)? = nil) -> (Result, DriftController) {
        let chunk = 1024, quantum = 512, rate = 48_000.0
        func target(_ delay: Int) -> Int { DriftController.target(delayFrames: delay, chunkFrames: chunk, quantumFrames: quantum) }
        var controller = DriftController(targetFrames: target(delayFrames))
        var fill = 0
        var nextProduce = 0.0, nextConsume = 0.0
        var result = Result()
        var started = false
        var lastChange = -100.0
        while min(nextProduce, nextConsume) < seconds {
            if nextProduce <= nextConsume {
                fill += chunk
                nextProduce += Double(chunk) / rate * (1 + skew)
                continue
            }
            let now = nextConsume
            if let (at, newDelay) = changeDelayAt, now >= at, controller.targetFrames != target(newDelay) {
                controller.targetFrames = target(newDelay)
                lastChange = now
            }
            switch controller.plan(available: fill, request: quantum) {
            case .silence:
                if started && now - lastChange > 2 { result.underrunsAfterStart += 1 }
            case let .play(discard, consume):
                started = true
                fill -= discard + consume
            }
            if started && now > 5 && now - lastChange > 10 {
                result.maxError = max(result.maxError, abs(controller.averageFill - Double(controller.targetFrames)))
            }
            nextConsume += Double(quantum) / rate
        }
        result.finalFill = controller.averageFill
        return (result, controller)
    }

    @Test(arguments: [0.0, 0.001, -0.001, 0.003])
    func holdsTargetForThirtyMinutes(skew: Double) {
        let (result, controller) = simulate(seconds: 1800, skew: skew, delayFrames: 0)
        #expect(result.underrunsAfterStart == 0)
        // ±20 ms acceptance criterion
        #expect(result.maxError < 960, "max error \(result.maxError) frames")
        #expect(!controller.priming)
    }

    @Test func raisingDelayReprimesQuickly() {
        let (result, controller) = simulate(seconds: 60, skew: 0.001, delayFrames: 0, changeDelayAt: (20, 24_000))
        #expect(abs(controller.averageFill - Double(controller.targetFrames)) < 960)
        #expect(result.underrunsAfterStart == 0)
    }

    @Test func loweringDelayJumpsDown() {
        let (_, controller) = simulate(seconds: 60, skew: -0.001, delayFrames: 24_000, changeDelayAt: (20, 0))
        #expect(abs(controller.averageFill - Double(controller.targetFrames)) < 960)
    }
}
