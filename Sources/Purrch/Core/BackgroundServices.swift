import Foundation

/// Starts the services that must run even while the notch is closed.
@MainActor
enum Services {
    static func startBackground() {
        ClipboardService.shared.start()
        NowPlayingService.shared.start()
        BrowserMediaService.shared.start()
        DownloadsService.shared.start()
        ScreenTimeService.shared.start()
        WeatherService.shared.start()
    }
}
