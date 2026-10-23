using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Purrch.Core;

namespace Purrch.Features.Media;

/// The link between Purrch and its browser extension. The browser starts `Purrch.exe chrome-extension://…/`
/// as its native messaging host; that copy (see <see cref="RunHost"/>) relays the browser's length-prefixed
/// JSON messages over a named pipe to the Purrch that is running, which serves the pipe here. Each browser
/// profile with the extension is one connection.
public sealed class BrowserBridge
{
    public static BrowserBridge Shared { get; } = new();

    /// Pipes are machine-wide, so the name carries the Windows session, like the command pipe. Only the same
    /// user can open it (CurrentUserOnly on both ends).
    private static readonly string PipeName = $"Purrch.Browser.{Process.GetCurrentProcess().SessionId}";
    /// Largest message accepted from a browser; a Sharp preview frame is a few hundred KB.
    private const int MaxMessage = 16 * 1024 * 1024;

    /// One browser profile running the extension.
    public sealed class Connection
    {
        private readonly SemaphoreSlim _writing = new(1, 1);

        internal Connection(int id, Stream stream)
        {
            Id = id;
            Stream = stream;
        }

        public int Id { get; }
        internal Stream Stream { get; }
        /// What the extension calls its browser ("Chrome", "Edge", "Brave" or "Chromium").
        internal string? Hello { get; set; }
        /// What the bridge saw as its parent process ("Vivaldi" and the rest), which can tell Vivaldi apart.
        internal string? Parent { get; set; }

        public string Browser => Hello is null or "" ? Parent ?? "Browser"
            : Hello == "Chromium" && Parent is { Length: > 0 } and not "Chrome" ? Parent
            : Hello;

        internal async Task WriteAsync(byte[] payload)
        {
            await _writing.WaitAsync().ConfigureAwait(false);
            try { await WriteMessageAsync(Stream, payload).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { /* gone; the reader cleans up */ }
            finally { _writing.Release(); }
        }
    }

    private readonly List<Connection> _connections = new();
    private bool _listening;
    private int _nextId;

    /// A browser connected, said which it is, or went away. Raised on the UI thread.
    public event Action? Changed;
    /// A message from a browser, raised on the pipe's own thread so frames can be decoded there; handlers
    /// move to the UI thread themselves.
    public event Action<Connection, JsonElement>? MessageReceived;

    public IReadOnlyList<Connection> Connections => _connections;

    /// "Chrome, Edge" for the Settings status line.
    public IReadOnlyList<string> ConnectedBrowsers => _connections.Select(c => c.Browser).Distinct().ToList();

    private BrowserBridge() { }

    /// Serves the pipe for as long as Purrch runs. Listening writes nothing anywhere; without "Connect browser"
    /// no browser knows to start the bridge, so nothing ever connects.
    public void Start()
    {
        if (_listening) return;
        _listening = true;
        _ = Listen();
    }

    public void Send(int connectionId, object message)
    {
        var connection = _connections.FirstOrDefault(c => c.Id == connectionId);
        if (connection == null) return;
        _ = connection.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(message));
    }

    /// Hangs up on every browser, for Disconnect in Settings. The extensions try again later and, with the
    /// host unregistered, find nothing.
    public void DropAll()
    {
        foreach (var connection in _connections.ToList()) connection.Stream.Dispose();
    }

    private async Task Listen()
    {
        while (true)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                pipe?.Dispose();
                Log.Write($"Browser pipe: {e.Message}");
                await Task.Delay(1000).ConfigureAwait(false);
                continue;
            }
            _ = Serve(pipe);
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        var connection = new Connection(Interlocked.Increment(ref _nextId), pipe);
        OnUi(() =>
        {
            _connections.Add(connection);
            Changed?.Invoke();
        });
        // Any message tells the extension Purrch is there, so it stops backing off.
        _ = connection.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "welcome" }));
        try
        {
            while (await ReadMessageAsync(pipe).ConfigureAwait(false) is { } payload)
            {
                JsonElement message;
                using (var document = JsonDocument.Parse(payload)) message = document.RootElement.Clone();
                if (message.ValueKind != JsonValueKind.Object) continue;
                var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is "hello" or "bridge")
                {
                    var name = message.TryGetProperty("browser", out var b) ? b.GetString() : null;
                    OnUi(() =>
                    {
                        if (type == "hello") connection.Hello = name;
                        else connection.Parent = name;
                        Changed?.Invoke();
                    });
                    continue;
                }
                MessageReceived?.Invoke(connection, message);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or ObjectDisposedException or InvalidDataException)
        {
            // The browser closed, or Disconnect hung up.
        }
        finally
        {
            pipe.Dispose();
            OnUi(() =>
            {
                _connections.Remove(connection);
                Changed?.Invoke();
            });
        }
    }

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    // MARK: - Wire format

    /// Native messaging's framing, used on the pipe too: a 32-bit little-endian length, then UTF-8 JSON.
    /// Null at a clean end of stream.
    private static async Task<byte[]?> ReadMessageAsync(Stream stream)
    {
        var header = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await stream.ReadAsync(header.AsMemory(read)).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessage) throw new InvalidDataException($"Message of {length} bytes");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteMessageAsync(Stream stream, byte[] payload)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    // MARK: - Native host

    /// True when a browser started this copy of Purrch as its native messaging host: it passes the calling
    /// extension's origin ("chrome-extension://<id>/") as the first argument.
    public static bool IsHostLaunch(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));

    /// Runs the bridge: browser (stdin / stdout) to the running Purrch (pipe) and back, until either side
    /// closes. Never opens a window. Returns the exit code.
    public static int RunHost()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            // The extension can't tell Vivaldi from Chromium; the process that started this copy can.
            if (BrowserFromParent() is { } browser)
                WriteMessageAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { type = "bridge", browser })).GetAwaiter().GetResult();
            // Both sides speak the same framing, so the bytes are copied as they come.
            var up = input.CopyToAsync(pipe);
            var down = CopyFlushingAsync(pipe, output);
            Task.WaitAny(up, down);
            return 0;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or AggregateException)
        {
            // Purrch isn't running (or just quit): the extension tries again later. Not logged, because the
            // extension retries every minute while Purrch is closed.
            return 1;
        }
    }

    /// Copies the pipe to stdout, flushing after each read so the browser gets every message at once.
    private static async Task CopyFlushingAsync(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await from.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
            await to.FlushAsync().ConfigureAwait(false);
        }
    }

    private static readonly Dictionary<string, string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["brave"] = "Brave",
        ["vivaldi"] = "Vivaldi",
    };

    /// The browser among this process's ancestors. Chromium starts hosts through cmd.exe, so it is the
    /// grandparent; a few levels are checked in case that changes.
    private static string? BrowserFromParent()
    {
        var pid = Environment.ProcessId;
        for (var level = 0; level < 4; level++)
        {
            pid = ParentOf(pid);
            if (pid <= 0) return null;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (BrowserProcesses.TryGetValue(process.ProcessName, out var name)) return name;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info,
                                                        int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static int ParentOf(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? info.InheritedFromUniqueProcessId.ToInt32()
                : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
