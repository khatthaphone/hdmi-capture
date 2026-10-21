#if DIRECT_DISTRIBUTION
import Sparkle
import SwiftUI

/// Sparkle updates for the direct-download (notarized DMG) build. The App Store build has no
/// updater: the store updates it.
@MainActor
final class Updates {
    static let shared = Updates()
    private let controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: nil, userDriverDelegate: nil)

    func checkForUpdates() {
        controller.checkForUpdates(nil)
    }
}

/// HDMI Captain › Check for Updates…
struct UpdateCommands: Commands {
    var body: some Commands {
        CommandGroup(after: .appInfo) {
            Button("Check for Updates…") { Updates.shared.checkForUpdates() }
        }
    }
}
#endif
