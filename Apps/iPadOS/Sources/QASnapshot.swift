#if DEBUG
import CaptureCore
import UIKit

/// QA: `xcrun devicectl device notification post --device <id> --name com.example.hdmicaptain.qa.snapshot`
/// saves `Documents/qa-ui.png` (the window as shown) and `Documents/qa-frame.png` (the raw source frame);
/// fetch them with `make ipad-snapshot`.
@MainActor
enum QASnapshot {
    static let notification = "com.example.hdmicaptain.qa.snapshot"
    private static weak var app: AppModel?

    static func install(app: AppModel) {
        self.app = app
        CFNotificationCenterAddObserver(
            CFNotificationCenterGetDarwinNotifyCenter(), nil,
            { _, _, _, _, _ in DispatchQueue.main.async { MainActor.assumeIsolated { QASnapshot.save() } } },
            notification as CFString, nil, .deliverImmediately
        )
    }

    private static func save() {
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let window = UIApplication.shared.connectedScenes
            .compactMap { ($0 as? UIWindowScene)?.keyWindow }
            .first
        if let window {
            let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
                window.drawHierarchy(in: window.bounds, afterScreenUpdates: false)
            }
            try? image.pngData()?.write(to: documents.appendingPathComponent("qa-ui.png"))
        }
        if let frame = app?.currentViewer?.renderer?.currentFrame, let data = FrameSnapshot.pngData(from: frame) {
            try? data.write(to: documents.appendingPathComponent("qa-frame.png"))
        }
    }
}
#endif
