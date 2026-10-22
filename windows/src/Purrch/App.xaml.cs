using System.Windows;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch;

public partial class App : Application
{
    private const string MutexName = @"Local\Purrch.SingleInstance";
    private const string OpenEventName = @"Local\Purrch.Open";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _openEvent;
    private TrayIcon? _tray;

    /// Raised on the UI thread when Purrch is launched a second time.
    public event Action? OpenRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running: ask that copy to open its panel and leave.
            try { EventWaitHandle.OpenExisting(OpenEventName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
        ThreadPool.RegisterWaitForSingleObject(_openEvent, (_, _) =>
            Dispatcher.BeginInvoke(() => OpenRequested?.Invoke()), null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
        Log.Write("Purrch started");

        var theme = AppSettings.Shared.Theme;
        ThemeResources.Apply(theme, theme.Accent);
        new NotchWindow().Show();
        _tray = new TrayIcon();
        BackgroundServices.Start();
        OpenRequested += () => NotchState.Shared.Present();
        AppSettings.Shared.ApplyStartupDefault();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _openEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the pill alive; one broken module shouldn't take the whole app down.
        Log.Write($"Unhandled: {e.Exception}");
        e.Handled = true;
    }
}
