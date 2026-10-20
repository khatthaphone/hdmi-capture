import CaptureCore
import SwiftUI

@main
struct HDMICaptainApp: App {
    @State private var app = AppModel()
    @State private var store = EntitlementStore()

    init() {
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
                .persistentSystemOverlays(.hidden)
                .statusBarHidden()
        }
    }
}
