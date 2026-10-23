using Purrch.Features.Clipboard;
using Purrch.Features.Downloads;
using Purrch.Features.Media;
using Purrch.Features.Notes;
using Purrch.Features.ScreenTime;
using Purrch.Features.Timers;
using Purrch.Features.Weather;

namespace Purrch.Core;

/// Services that must run whether or not the panel is open (they feed the closed pill's live activities).
public static class BackgroundServices
{
    public static void Start()
    {
        _ = MessageService.Shared;
        _ = TimerService.Shared;
        DownloadsService.Shared.Start();
        ClipboardService.Shared.Start();
        NowPlayingService.Shared.Start();
        ScreenTimeService.Shared.Start();
        // Keeps the forecast fresh for Home's chip. It never calls Stop, so this counts as a user for good.
        WeatherService.Shared.Start();
    }

    /// Writes anything still waiting to be saved before Purrch quits.
    public static void Stop()
    {
        NotesStore.Shared.Flush();
        ScratchpadStore.Shared.Flush();
        MessageService.Shared.Flush();
        AmbienceEngine.Shutdown();
        DownloadsService.Shared.Flush();
        ScreenTimeService.Shared.Flush();
    }
}
