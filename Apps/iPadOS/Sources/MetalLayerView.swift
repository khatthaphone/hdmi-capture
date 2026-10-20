import CaptureCore
import SwiftUI
import UIKit

/// Hosts the renderer's CAMetalLayer. Tap → `onClick` (show/hide the controls), double tap →
/// `onDoubleClick`, and trackpad/mouse hover reports the pointer like the Mac view does.
final class MetalLayerView: UIView {
    private let renderer: MetalRenderer
    var onDoubleClick: (() -> Void)?
    var onClick: (() -> Void)?
    /// Pointer distance from the bottom edge (nil when it leaves the view) and the view height.
    var onPointer: ((CGFloat?, CGFloat) -> Void)?

    override class var layerClass: AnyClass { CAMetalLayer.self }

    init(renderer: MetalRenderer) {
        self.renderer = renderer
        super.init(frame: .zero)
        isOpaque = true
        backgroundColor = .black
        renderer.attach(metalLayer)

        let doubleTap = UITapGestureRecognizer(target: self, action: #selector(doubleTapped))
        doubleTap.numberOfTapsRequired = 2
        let tap = UITapGestureRecognizer(target: self, action: #selector(tapped))
        tap.require(toFail: doubleTap)
        addGestureRecognizer(doubleTap)
        addGestureRecognizer(tap)
        addGestureRecognizer(UIHoverGestureRecognizer(target: self, action: #selector(hovered(_:))))
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    private var metalLayer: CAMetalLayer { layer as! CAMetalLayer }

    override func layoutSubviews() {
        super.layoutSubviews()
        updateDrawableSize()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        updateDrawableSize()
    }

    private func updateDrawableSize() {
        let scale = window?.screen.scale ?? traitCollection.displayScale
        metalLayer.contentsScale = scale
        let size = CGSize(width: bounds.width * scale, height: bounds.height * scale)
        guard size.width > 0, size.height > 0, size != metalLayer.drawableSize else { return }
        metalLayer.drawableSize = size
        renderer.redraw()
    }

    @objc private func tapped() { onClick?() }
    @objc private func doubleTapped() { onDoubleClick?() }

    @objc private func hovered(_ recognizer: UIHoverGestureRecognizer) {
        switch recognizer.state {
        case .began, .changed:
            onPointer?(bounds.height - recognizer.location(in: self).y, bounds.height)
        default:
            onPointer?(nil, bounds.height)
        }
    }
}

/// Same API as the macOS `MetalPreview`; window-chrome options have no meaning on iPad.
struct MetalPreview: UIViewRepresentable {
    let renderer: MetalRenderer
    var autoHidesWindowButtons = true
    var keepsChromeVisible = false
    var onClick: (() -> Void)?
    var onPointer: ((CGFloat?, CGFloat) -> Void)?
    let onDoubleClick: () -> Void

    func makeUIView(context: Context) -> MetalLayerView {
        let view = MetalLayerView(renderer: renderer)
        updateUIView(view, context: context)
        return view
    }

    func updateUIView(_ view: MetalLayerView, context: Context) {
        view.onDoubleClick = onDoubleClick
        view.onClick = onClick
        view.onPointer = onPointer
    }
}
