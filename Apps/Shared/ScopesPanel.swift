import CaptureCore
import Observation
import SwiftUI

/// Analyzes the active viewer's on-screen frame ~10× per second while the panel is open.
@MainActor
@Observable
final class ScopesModel {
    private(set) var data: ScopeData?
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var busy = false

    func start(frame: @escaping @MainActor () -> CVPixelBuffer?) {
        stop()
        let timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, !self.busy, let buffer = frame() else { return }
                self.busy = true
                Task.detached(priority: .utility) {
                    let data = ScopeData.analyze(buffer)
                    await MainActor.run {
                        self.data = data
                        self.busy = false
                    }
                }
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }
}

struct ScopesPanel: View {
    @Environment(AppModel.self) private var app
    @State private var model = ScopesModel()

    var body: some View {
        HStack(spacing: 10) {
            scope("Waveform") { WaveformView(data: model.data) }
            scope("Vectorscope") { VectorscopeView(data: model.data) }
                .aspectRatio(1, contentMode: .fit)
            scope("Histogram") { HistogramView(data: model.data) }
        }
        .padding(10)
        .background(.black)
        .frame(minWidth: 720, minHeight: 240)
        .onAppear {
            model.start { [weak app] in
                app?.activeViewer?.renderer?.currentFrame ?? app?.mainViewer?.renderer?.currentFrame
            }
        }
        .onDisappear { model.stop() }
    }

    private func scope<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            content()
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color(white: 0.06))
                .clipShape(RoundedRectangle(cornerRadius: 4))
        }
    }
}

private struct WaveformView: View {
    let data: ScopeData?

    var body: some View {
        ZStack {
            if let image = data?.waveformImage() {
                Image(decorative: image, scale: 1).resizable().interpolation(.medium)
            }
            Canvas { context, size in
                // Graticule at 0/25/50/75/100 % of the video range.
                let full = data?.fullRange ?? false
                let lo = full ? 0.0 : 16.0, hi = full ? 255.0 : 235.0
                for step in 0...4 {
                    let code = lo + (hi - lo) * Double(step) / 4
                    let y = size.height * (1 - code / 255)
                    context.stroke(Path { $0.move(to: CGPoint(x: 0, y: y)); $0.addLine(to: CGPoint(x: size.width, y: y)) },
                                   with: .color(.white.opacity(step == 0 || step == 4 ? 0.35 : 0.15)), lineWidth: 1)
                    context.draw(Text("\(step * 25)").font(.system(size: 9)).foregroundStyle(.white.opacity(0.5)),
                                 at: CGPoint(x: 10, y: y - 6))
                }
            }
        }
    }
}

private struct VectorscopeView: View {
    let data: ScopeData?

    var body: some View {
        ZStack {
            if let image = data?.vectorscopeImage() {
                Image(decorative: image, scale: 1).resizable().interpolation(.medium)
            }
            Canvas { context, size in
                let center = CGPoint(x: size.width / 2, y: size.height / 2)
                context.stroke(Path(ellipseIn: CGRect(x: 0, y: 0, width: size.width, height: size.height).insetBy(dx: size.width * 0.06, dy: size.height * 0.06)),
                               with: .color(.white.opacity(0.2)))
                context.stroke(Path { $0.move(to: CGPoint(x: center.x, y: 0)); $0.addLine(to: CGPoint(x: center.x, y: size.height))
                                      $0.move(to: CGPoint(x: 0, y: center.y)); $0.addLine(to: CGPoint(x: size.width, y: center.y)) },
                               with: .color(.white.opacity(0.12)))
                for target in ScopeData.barTargets {
                    let p = CGPoint(x: target.point.x * size.width, y: target.point.y * size.height)
                    let box = CGRect(x: p.x - 5, y: p.y - 5, width: 10, height: 10)
                    context.stroke(Path(box), with: .color(.white.opacity(0.6)))
                    context.draw(Text(target.name).font(.system(size: 9)).foregroundStyle(.white.opacity(0.6)),
                                 at: CGPoint(x: p.x, y: p.y - 12))
                }
            }
        }
    }
}

private struct HistogramView: View {
    let data: ScopeData?

    var body: some View {
        if let image = data?.histogramImage() {
            Image(decorative: image, scale: 1).resizable().interpolation(.medium)
        }
    }
}
