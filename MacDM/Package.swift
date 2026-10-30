// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MacDM",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "MacDM", targets: ["MacDM"]),
        .executable(name: "macdm-cli", targets: ["macdm-cli"]),
        .library(name: "DownloadEngine", targets: ["DownloadEngine"]),
    ],
    targets: [
        .target(name: "DownloadEngine"),
        .executableTarget(name: "MacDM", dependencies: ["DownloadEngine"]),
        .executableTarget(name: "macdm-cli", dependencies: ["DownloadEngine"]),
    ],
    swiftLanguageModes: [.v5]
)
