import CaptureCore
import SwiftUI

/// Small "how to get the picture back" drawing for the Paused screen: what the iPad looks like
/// now → what to do. Drawn with shapes so it stays crisp at any size and in any window.
struct PauseIllustration: View {
    let reason: PauseReason

    var body: some View {
        switch reason {
        case .multitasking:
            Steps(from: Tablet(screen: .split), to: Tablet(screen: .video))
        case .background:
            Steps(from: Tablet(screen: .home), to: Tablet(screen: .video))
        case .inUseByAnotherApp:
            symbol("rectangle.2.swap")
        case .tooHot:
            symbol("thermometer.sun.fill")
                .symbolRenderingMode(.multicolor)
        case .other:
            symbol("pause.circle")
        }
    }

    private func symbol(_ name: String) -> some View {
        Image(systemName: name)
            .font(.system(size: 56, weight: .regular))
            .frame(height: 84)
    }
}

/// Before → after, with a gently nudging arrow.
private struct Steps<From: View, To: View>: View {
    let from: From
    let to: To

    var body: some View {
        HStack(spacing: 18) {
            from
            Image(systemName: "arrow.right")
                .font(.title2.weight(.semibold))
                .foregroundStyle(.white.opacity(0.8))
                .phaseAnimator([0.0, 6.0]) { arrow, offset in
                    arrow.offset(x: offset)
                } animation: { _ in .easeInOut(duration: 0.8) }
            to
        }
        .accessibilityHidden(true)
    }
}

/// A landscape iPad outline with a simplified screen.
private struct Tablet: View {
    enum Screen { case split, home, video }
    let screen: Screen

    var body: some View {
        RoundedRectangle(cornerRadius: 11, style: .continuous)
            .stroke(.white.opacity(0.85), lineWidth: 2.5)
            .frame(width: 116, height: 82)
            .overlay { content.padding(7) }
    }

    @ViewBuilder private var content: some View {
        switch screen {
        case .split:
            HStack(spacing: 3) {
                VideoPane(symbol: "pause.fill")
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .fill(.white.opacity(0.22))
                    .frame(width: 34)
            }
        case .home:
            Grid(horizontalSpacing: 7, verticalSpacing: 7) {
                ForEach(0..<2, id: \.self) { _ in
                    GridRow {
                        ForEach(0..<4, id: \.self) { _ in
                            RoundedRectangle(cornerRadius: 3, style: .continuous)
                                .fill(.white.opacity(0.3))
                                .frame(width: 16, height: 16)
                        }
                    }
                }
            }
        case .video:
            VideoPane(symbol: "play.fill")
        }
    }
}

/// Stand-in for the HDMI picture.
private struct VideoPane: View {
    let symbol: String

    var body: some View {
        RoundedRectangle(cornerRadius: 4, style: .continuous)
            .fill(LinearGradient(colors: [.indigo, .teal], startPoint: .topLeading, endPoint: .bottomTrailing))
            .overlay {
                Image(systemName: symbol)
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(.white)
            }
    }
}
