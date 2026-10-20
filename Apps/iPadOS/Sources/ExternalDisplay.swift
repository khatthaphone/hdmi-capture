import CaptureCore
import UIKit

/// A TV or monitor connected to the iPad shows the clean picture (no controls, stats or guides)
/// while the iPad screen stays the controller. iPadOS creates this scene when a display connects.
final class ExternalDisplaySceneDelegate: NSObject, UIWindowSceneDelegate {
    var window: UIWindow?
    private var feed: ExternalDisplayFeed?

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options: UIScene.ConnectionOptions) {
        guard let scene = scene as? UIWindowScene else { return }
        MainActor.assumeIsolated {
            guard let renderer = MetalRenderer() else { return }
            let feed = ExternalDisplayFeed(renderer: renderer)
            let window = UIWindow(windowScene: scene)
            window.rootViewController = feed
            window.isHidden = false
            self.window = window
            self.feed = feed
            ExternalDisplayFeed.log("External display connected: \(scene.screen.bounds.size) @\(scene.screen.scale)x")
        }
    }

    func sceneDidDisconnect(_ scene: UIScene) {
        MainActor.assumeIsolated {
            feed?.stop()
            feed = nil
            window = nil
            ExternalDisplayFeed.log("External display disconnected")
        }
    }
}

/// Renders the main viewer's card (or test pattern) with its own renderer, attached to the same
/// capture session; capture itself is never touched when the display comes or goes.
@MainActor
final class ExternalDisplayFeed: UIViewController {
    /// Set by the app at launch.
    static weak var app: AppModel?

    private let renderer: MetalRenderer
    private var attachedID: String?
    private var timer: Timer?

    init(renderer: MetalRenderer) {
        self.renderer = renderer
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func loadView() {
        view = MetalLayerView(renderer: renderer)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        sync()
        // Follow card switches and the main viewer's look (scale, picture, LUT) twice a second.
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sync() }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        if let attachedID { Self.app?.sessions[attachedID]?.detach(renderer) }
        attachedID = nil
    }

    private func sync() {
        guard let app = Self.app, let viewer = app.currentViewer else { return }
        if let pattern = viewer.testPattern {
            if let attachedID { app.sessions[attachedID]?.detach(renderer) }
            attachedID = nil
            renderer.showStill(AppModel.patternBuffer(pattern))
            return
        }
        let id = viewer.session?.deviceID
        guard id != attachedID else { return }
        if let attachedID { app.sessions[attachedID]?.detach(renderer) }
        renderer.clear()
        attachedID = id
        viewer.session?.attach(renderer)
    }

    nonisolated static func log(_ message: String) {
        CaptureEngine.log.info("\(message, privacy: .public)")
    }
}
