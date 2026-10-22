using Purrch.Features.Timers;

namespace Purrch.Core;

/// Services that must run whether or not the panel is open (they feed the closed pill's live activities).
public static class BackgroundServices
{
    public static void Start()
    {
        _ = TimerService.Shared;
    }
}
