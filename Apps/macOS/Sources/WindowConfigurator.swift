import AppKit
import SwiftUI

/// Applies viewer preferences (float, all Spaces, clean mode, aspect lock) to the hosting NSWindow.
struct WindowConfigurator: NSViewRepresentable {
    let prefs: ViewerPrefs
    let aspect: CGSize?
    let onWindow: (NSWindow) -> Void

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> WindowTrackingView {
        let view = WindowTrackingView()
        view.onWindow = { window in
            onWindow(window)
            context.coordinator.apply(prefs: prefs, aspect: aspect, to: window)
        }
        return view
    }

    func updateNSView(_ view: WindowTrackingView, context: Context) {
        guard let window = view.window else { return }
        context.coordinator.apply(prefs: prefs, aspect: aspect, to: window)
    }

    final class Coordinator {
        private var appliedAspect: CGSize?
        private var fullScreenObserver: NSObjectProtocol?
        private var last: (prefs: ViewerPrefs, aspect: CGSize?)?

        func apply(prefs: ViewerPrefs, aspect: CGSize?, to window: NSWindow) {
            last = (prefs, aspect)
            if fullScreenObserver == nil {
                // Leaving full screen can drop the aspect constraint; put it back.
                fullScreenObserver = NotificationCenter.default.addObserver(
                    forName: NSWindow.didExitFullScreenNotification, object: window, queue: .main
                ) { [weak self, weak window] _ in
                    guard let self, let window, let last = self.last else { return }
                    self.appliedAspect = nil
                    self.apply(prefs: last.prefs, aspect: last.aspect, to: window)
                }
            }
            window.backgroundColor = .black
            window.isMovableByWindowBackground = true
            window.level = prefs.floatOnTop ? .floating : .normal

            var behavior = window.collectionBehavior
            behavior.insert(.fullScreenPrimary)
            if prefs.allSpaces {
                behavior.insert(.canJoinAllSpaces)
            } else {
                behavior.remove(.canJoinAllSpaces)
            }
            window.collectionBehavior = behavior

            // QuickTime-style: the video fills the whole window under a transparent title bar,
            // so the content area has exactly the source's aspect ratio.
            let isFullScreen = window.styleMask.contains(.fullScreen)
            window.styleMask.insert(.fullSizeContentView)
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            for button in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
                window.standardWindowButton(button)?.isHidden = prefs.cleanMode
            }

            guard !isFullScreen else { return }
            if prefs.lockAspect, let aspect, aspect.width > 0, aspect.height > 0 {
                window.contentAspectRatio = aspect
                if appliedAspect != aspect, let content = window.contentView {
                    // Snap the window to the new source shape, keeping its width.
                    let width = content.frame.width
                    window.setContentSize(NSSize(width: width, height: (width * aspect.height / aspect.width).rounded()))
                }
                appliedAspect = aspect
            } else if appliedAspect != nil {
                window.contentResizeIncrements = NSSize(width: 1, height: 1)
                appliedAspect = nil
            }
        }
    }
}

final class WindowTrackingView: NSView {
    var onWindow: ((NSWindow) -> Void)?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let window { onWindow?(window) }
    }
}
