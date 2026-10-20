import CaptureCore
import SwiftUI

@main
struct HDMICaptainApp: App {
    @State private var app = AppModel()
    @State private var store = EntitlementStore()

    init() {
        ExternalDisplayFeed.app = _app.wrappedValue
        #if DEBUG
        QASnapshot.install(app: _app.wrappedValue)
        #endif
    }

    var body: some Scene {
        WindowGroup {
            ViewerRoot(app: app, source: .automatic)
                .environment(app)
                .environment(store)
                .sheet(isPresented: $store.isShowingUpgrade) {
                    UpgradeView().environment(store)
                }
                .sheet(isPresented: $app.isShowingSettings) {
                    SettingsSheet()
                        .environment(app)
                        .environment(store)
                }
                .persistentSystemOverlays(.hidden)
                .statusBarHidden()
        }
        .commands { IPadCommands(app: app, store: store) }
    }
}
