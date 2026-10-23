using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch;

public partial class App : Application
{
    private const string MutexName = @"Local\Purrch.SingleInstance";
    /// Pipes are machine-wide, so the name carries the Windows session, like the Local\ mutex.
    private static readonly string CommandPipe = $"Purrch.Command.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    private Mutex? _instanceMutex;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // "Purrch.exe timers", "Purrch.exe settings" or "Purrch.exe close", like the Mac's script hook.
        var command = e.Args.FirstOrDefault(a => !a.StartsWith('-')) ?? "";
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running: hand that copy the command (none means open the panel) and leave.
            SendCommand(command);
            Shutdown();
            return;
        }
        _ = ListenForCommands();

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
        Log.Write("Purrch started");

        var theme = AppSettings.Shared.Theme;
        ThemeResources.Apply(theme, theme.Accent);
        // Before the timers schedule anything, so a notification click that launched Purrch isn't missed.
        // The panel opens on its module once startup has finished.
        Toasts.Start();
        new NotchWindow().Show();
        _tray = new TrayIcon();
        BackgroundServices.Start();
        AppSettings.Shared.ApplyStartupDefault();
        if (command.Length > 0) Run(command);

        // On first run the welcome screen comes down by itself, so nobody has to find the pill first.
        if (!AppSettings.Shared.HasCompletedOnboarding)
        {
            var welcome = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.6) };
            welcome.Tick += (_, _) => { welcome.Stop(); NotchState.Shared.Present(); };
            welcome.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) BackgroundServices.Stop(); // a second copy that quit at once has nothing to save
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// Opens a module ("timers"), Settings ("settings"), or closes the panel ("close"); anything else opens it.
    private static void Run(string command)
    {
        var state = NotchState.Shared;
        switch (command.ToLowerInvariant())
        {
            case "close":
                state.ForceClose();
                break;
            case "settings":
                state.Present(settings: true);
                break;
            default:
                var module = ModuleInfo.All.Where(m => string.Equals(m.Key(), command, StringComparison.OrdinalIgnoreCase))
                    .Cast<ModuleId?>().FirstOrDefault();
                state.Present(module);
                break;
        }
    }

    private static void SendCommand(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", CommandPipe, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(command);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't reach the running Purrch: {e.Message}");
        }
    }

    /// Serves one command at a time from later launches, for as long as Purrch runs.
    private async Task ListenForCommands()
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(CommandPipe, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                var command = (await reader.ReadLineAsync().ConfigureAwait(false))?.Trim() ?? "";
                _ = Dispatcher.BeginInvoke(() => Run(command));
            }
            catch (IOException e)
            {
                Log.Write($"Command pipe: {e.Message}");
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the pill alive; one broken module shouldn't take the whole app down.
        Log.Write($"Unhandled: {e.Exception}");
        e.Handled = true;
    }
}
