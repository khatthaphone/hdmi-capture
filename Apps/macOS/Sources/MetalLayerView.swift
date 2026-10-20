import AppKit
import CaptureCore
import SwiftUI

/// Hosts the renderer's CAMetalLayer. Also handles double-click → full screen, window dragging,
/// cursor auto-hide, and pausing drawing while the window is fully covered.
final class MetalLayerView: NSView {
    private let renderer: MetalRenderer
    var onDoubleClick: (() -> Void)?
    /// Fade the window buttons with the cursor (off in clean mode, where they stay hidden).
    var autoHidesWindowButtons = true
    private var hideCursor: DispatchWorkItem?
    private var occlusionObserver: NSObjectProtocol?
    private var trackingArea: NSTrackingArea?

    init(renderer: MetalRenderer) {
        self.renderer = renderer
        super.init(frame: .zero)
        wantsLayer = true
        layerContentsRedrawPolicy = .never
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    private var metalLayer: CAMetalLayer { layer as! CAMetalLayer }

    override func makeBackingLayer() -> CALayer {
        let layer = CAMetalLayer()
        renderer.attach(layer)
        return layer
    }

    override var isOpaque: Bool { true }
    override var mouseDownCanMoveWindow: Bool { true }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        updateDrawableSize()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        updateDrawableSize()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let occlusionObserver { NotificationCenter.default.removeObserver(occlusionObserver) }
        occlusionObserver = nil
        guard let window else { return }
        occlusionObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.didChangeOcclusionStateNotification, object: window, queue: .main
        ) { [weak self, weak window] _ in
            guard let window else { return }
            self?.renderer.setVisible(window.occlusionState.contains(.visible))
        }
        updateDrawableSize()
    }

    private func updateDrawableSize() {
        let scale = window?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2
        metalLayer.contentsScale = scale
        let size = CGSize(width: bounds.width * scale, height: bounds.height * scale)
        guard size.width > 0, size.height > 0, size != metalLayer.drawableSize else { return }
        metalLayer.drawableSize = size
        renderer.redraw()
    }

    // MARK: Mouse

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self)
        addTrackingArea(area)
        trackingArea = area
    }

    override func mouseMoved(with event: NSEvent) {
        setWindowButtonsVisible(true)
        scheduleCursorHide()
    }

    override func mouseEntered(with event: NSEvent) {
        setWindowButtonsVisible(true)
        scheduleCursorHide()
    }

    override func mouseExited(with event: NSEvent) {
        hideCursor?.cancel()
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 {
            onDoubleClick?()
        } else {
            window?.performDrag(with: event)
        }
    }

    private func setWindowButtonsVisible(_ visible: Bool) {
        guard autoHidesWindowButtons, let window, !window.styleMask.contains(.fullScreen) else { return }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = visible ? 0.15 : 0.4
            for type in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
                window.standardWindowButton(type)?.animator().alphaValue = visible ? 1 : 0
            }
        }
    }

    private func scheduleCursorHide() {
        hideCursor?.cancel()
        let work = DispatchWorkItem { [weak self] in
            NSCursor.setHiddenUntilMouseMoves(true)
            self?.setWindowButtonsVisible(false)
        }
        hideCursor = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 2, execute: work)
    }
}

struct MetalPreview: NSViewRepresentable {
    let renderer: MetalRenderer
    var autoHidesWindowButtons = true
    let onDoubleClick: () -> Void

    func makeNSView(context: Context) -> MetalLayerView {
        let view = MetalLayerView(renderer: renderer)
        view.onDoubleClick = onDoubleClick
        view.autoHidesWindowButtons = autoHidesWindowButtons
        return view
    }

    func updateNSView(_ view: MetalLayerView, context: Context) {
        view.onDoubleClick = onDoubleClick
        view.autoHidesWindowButtons = autoHidesWindowButtons
    }
}
