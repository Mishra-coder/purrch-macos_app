using Purrch.Features.Clipboard;
using Purrch.Features.Notes;
using Purrch.Features.Timers;

namespace Purrch.Core;

/// Services that must run whether or not the panel is open (they feed the closed pill's live activities).
public static class BackgroundServices
{
    public static void Start()
    {
        _ = TimerService.Shared;
        ClipboardService.Shared.Start();
    }

    /// Writes anything still waiting to be saved before Purrch quits.
    public static void Stop()
    {
        NotesStore.Shared.Flush();
        ScratchpadStore.Shared.Flush();
    }
}
