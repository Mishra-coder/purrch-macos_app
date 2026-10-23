using System.IO;
using Microsoft.Win32;
using Purrch.Core;
using Purrch.Features.Insights;

namespace Purrch.Features.Assistant;

public enum ChatRole { User, Assistant, Notice }

public sealed record ChatMessage(ChatRole Role, string Text)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// The Assistant chat: a help assistant for Windows that knows a little about this PC.
public sealed class AssistantService
{
    public static AssistantService Shared { get; } = new();

    private readonly List<ChatMessage> _messages = new();
    private CancellationTokenSource? _cancel;

    public event Action? Changed;

    public IReadOnlyList<ChatMessage> Messages => _messages;
    public bool IsThinking { get; private set; }

    public void Clear()
    {
        _cancel?.Cancel();
        _cancel = null;
        IsThinking = false;
        _messages.Clear();
        Changed?.Invoke();
    }

    public async void Send(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || IsThinking) return;
        var history = _messages.Where(m => m.Role != ChatRole.Notice)
            .Select(m => new ChatTurn(m.Role == ChatRole.User, m.Text)).ToList();
        _messages.Add(new ChatMessage(ChatRole.User, text));
        IsThinking = true;
        Changed?.Invoke();

        var cancel = _cancel = new CancellationTokenSource();
        var settings = AppSettings.Shared;
        try
        {
            var reply = (await AiClient.CompleteAsync(settings.AiProvider, SystemPrompt(), history, text,
                webSearch: settings.AiProvider == AiProvider.Claude && settings.AssistantWebSearch, cancel.Token)).Trim();
            if (cancel.IsCancellationRequested) return;
            _messages.Add(reply.Length == 0
                ? new ChatMessage(ChatRole.Notice, "No answer came back. Try again.")
                : new ChatMessage(ChatRole.Assistant, reply));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (AiException e)
        {
            if (cancel.IsCancellationRequested) return;
            _messages.Add(new ChatMessage(ChatRole.Notice, e.Message));
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            Log.Write($"Assistant failed: {e}");
            _messages.Add(new ChatMessage(ChatRole.Notice, "Something went wrong. Try again."));
        }
        IsThinking = false;
        Changed?.Invoke();
    }

    private static string SystemPrompt()
    {
        var monitor = SystemMonitor.Shared;
        // A one-off reading: Start samples straight away, Stop leaves the System module's own sampling alone.
        monitor.Start();
        monitor.Stop();
        var battery = monitor.Battery.Present
            ? $"{Math.Round(monitor.Battery.Level * 100)}%{(monitor.Battery.IsCharging ? ", charging" : monitor.Battery.OnAC ? ", plugged in" : "")}"
            : "none (desktop)";
        var front = AppNames.Of(NotchState.Shared.PreviousApp) ?? "unknown";
        return $"""
            You are Purrch Assistant, a concise, friendly help assistant for Windows that lives at the top of the user's screen. Answer questions about using, fixing and tuning their PC. Prefer short answers: a sentence or two, then numbered steps or exact keyboard shortcuts / commands when useful. Use Markdown for **bold** and `code` only; no headings or tables. Mention Settings paths for the user's Windows version. Warn before anything destructive.

            Live context about this PC:
            - Windows: {SystemFormat.WindowsVersion()}
            - Model: {HardwareModel()}
            - Memory: {SystemFormat.Gb(monitor.MemoryUsed)} used of {SystemFormat.Gb(monitor.MemoryTotal)}
            - Free disk: {Format.Bytes(monitor.DiskFree)} on {monitor.DiskName}
            - Battery: {battery}
            - App in front: {front}
            """;
    }

    /// Placeholders some boards leave in the BIOS instead of a real name.
    private static readonly string[] OemPlaceholders =
        { "System manufacturer", "System Product Name", "To Be Filled By O.E.M.", "Default string", "Not Applicable" };

    /// Maker and model from the BIOS ("Dell Inc. XPS 13 9310"), like hw.model on the Mac.
    private static string HardwareModel()
    {
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var parts = new[] { "SystemManufacturer", "SystemProductName" }
                .Select(name => (bios?.GetValue(name) as string)?.Trim() ?? "")
                .Where(part => part.Length > 0 && !OemPlaceholders.Contains(part, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return parts.Count == 0 ? "unknown PC" : string.Join(" ", parts);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "unknown PC";
        }
    }
}
