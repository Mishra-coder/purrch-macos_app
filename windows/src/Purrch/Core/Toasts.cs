using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Purrch.Core;

/// Windows notifications, the counterpart of the Mac app's UNUserNotificationCenter: they play the
/// notification sound, stay in the notification centre, and clicking one opens Purrch on the module
/// that sent it. Scheduled ones are delivered by Windows, so they come even while Purrch isn't running.
public static class Toasts
{
    private const string ModuleArgument = "module";

    /// Call once at startup. A click that launched Purrch is delivered as soon as this subscribes.
    /// ToastNotificationManagerCompat.Uninstall is deliberately never called: it is meant for uninstallers,
    /// and on quit it would also wipe the water reminders Windows still has queued.
    public static void Start()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Write($"Notifications unavailable: {e.Message}");
        }
    }

    /// Shows a notification now.
    public static void Show(string title, string body, ModuleId module)
    {
        try
        {
            Content(title, body, module).Show();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write($"Notification failed: {e.Message}");
        }
    }

    /// Queues notifications that Windows shows at the given times, tagged with a group so they can be found again.
    public static void Schedule(string group, string title, string body, ModuleId module, IEnumerable<DateTimeOffset> times)
    {
        try
        {
            foreach (var time in times)
            {
                Content(title, body, module).Schedule(time, toast =>
                {
                    toast.Group = group;
                    // Unique per time; a shared tag would make each reminder replace the one before it.
                    toast.Tag = $"{group}-{time.ToUnixTimeSeconds()}";
                });
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write($"Scheduling notifications failed: {e.Message}");
        }
    }

    /// When the group's queued notifications are due, earliest first. Delivered ones drop off the list.
    public static IReadOnlyList<DateTimeOffset> Scheduled(string group)
    {
        try
        {
            return ToastNotificationManagerCompat.CreateToastNotifier().GetScheduledToastNotifications()
                .Where(t => t.Group == group)
                .Select(t => t.DeliveryTime)
                .OrderBy(t => t)
                .ToList();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Write($"Reading scheduled notifications failed: {e.Message}");
            return Array.Empty<DateTimeOffset>();
        }
    }

    /// Removes the group's queued notifications.
    public static void Unschedule(string group)
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            foreach (var toast in notifier.GetScheduledToastNotifications().Where(t => t.Group == group).ToList())
                notifier.RemoveFromSchedule(toast);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Write($"Removing scheduled notifications failed: {e.Message}");
        }
    }

    private static ToastContentBuilder Content(string title, string body, ModuleId module) =>
        new ToastContentBuilder()
            .AddArgument(ModuleArgument, module.ToString())
            .AddText(title)
            .AddText(body)
            .AddAudio(new Uri("ms-winsoundevent:Notification.Default"));

    /// Raised on a background thread, also when the click started Purrch.
    private static void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        ModuleId? module = null;
        try
        {
            if (ToastArguments.Parse(e.Argument).TryGetValue(ModuleArgument, out string name)
                && Enum.TryParse<ModuleId>(name, out var parsed))
                module = parsed;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Log.Write($"Unreadable notification click: {ex.Message}");
        }
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var target = module is { } m && AppSettings.Shared.IsEnabled(m) ? m : (ModuleId?)null;
            NotchState.Shared.Present(target);
        });
    }
}
