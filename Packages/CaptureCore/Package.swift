// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CaptureCore",
    platforms: [.macOS(.v14), .iOS(.v17)],
    products: [
        .library(name: "CaptureCore", targets: ["CaptureCore"]),
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-atomics.git", from: "1.2.0"),
    ],
    targets: [
        // Swift 5 language mode: AVFoundation's delegate/queue model doesn't map cleanly onto strict concurrency yet.
        .target(
            name: "CaptureCore",
            dependencies: [.product(name: "Atomics", package: "swift-atomics")],
            swiftSettings: [.swiftLanguageMode(.v5)]),
        .testTarget(name: "CaptureCoreTests", dependencies: ["CaptureCore"], swiftSettings: [.swiftLanguageMode(.v5)]),
    ]
)
