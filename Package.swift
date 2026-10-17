// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "Purrch",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Purrch",
            path: "Sources/Purrch",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("SwiftUI"),
                .linkedFramework("EventKit"),
                .linkedFramework("CoreLocation"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("IOKit"),
                .linkedFramework("ServiceManagement"),
            ]
        )
    ]
)
