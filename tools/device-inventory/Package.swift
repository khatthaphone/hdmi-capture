// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "device-inventory",
    platforms: [.macOS(.v14)],
    dependencies: [.package(path: "../../Packages/CaptureCore")],
    targets: [
        .executableTarget(
            name: "device-inventory",
            dependencies: [.product(name: "CaptureCore", package: "CaptureCore")],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
