using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using Purrch.Core;

namespace Purrch.Features.Media;

/// What "Connect browser" in Settings writes so the extension can reach Purrch, and what "Disconnect" removes.
/// Only those two buttons write anything; startup and the status line only read.
public static class BrowserSetup
{
    public const string HostName = "com.purrch.bridge";
    /// Pinned by the public key in the extension's manifest.json; the bridge accepts no other extension.
    public const string ExtensionId = "digijohbadkdamofjlcaobgpppepabem";

    private const string ResourcePrefix = "Purrch.Extension.";

    /// Where each Chromium browser looks for native messaging hosts, under HKEY_CURRENT_USER. Edge and Brave
    /// also read Chrome's; each gets its own anyway.
    private static readonly string[] BrowserKeys =
    {
        @"Software\Google\Chrome",
        @"Software\Microsoft\Edge",
        @"Software\BraveSoftware\Brave-Browser",
        @"Software\Vivaldi",
        @"Software\Chromium",
    };

    /// The unpacked extension, for the browser's Load unpacked.
    public static string ExtensionFolder => Path.Combine(AppPaths.Root, "extension");
    public static string BridgeFolder => Path.Combine(AppPaths.Root, "bridge");
    public static string ManifestPath => Path.Combine(BridgeFolder, HostName + ".json");

    private static string HostKey(string browser) => $@"{browser}\NativeMessagingHosts\{HostName}";

    public enum State
    {
        NotConnected,
        /// Registered, but for a Purrch.exe in another folder (moved or updated somewhere else).
        Moved,
        Registered,
    }

    public static State Current
    {
        get
        {
            if (!File.Exists(ManifestPath) || !BrowserKeys.Any(PointsHere)) return State.NotConnected;
            return SamePath(RegisteredExe(), Environment.ProcessPath) ? State.Registered : State.Moved;
        }
    }

    private static bool PointsHere(string browser)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HostKey(browser));
            return key?.GetValue("") is string path && SamePath(path, ManifestPath);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// The exe the bridge manifest names.
    private static string? RegisteredExe()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            return document.RootElement.TryGetProperty("path", out var path) ? path.GetString() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SamePath(string? a, string? b)
    {
        if (a == null || b == null) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// Writes the extension's files, the bridge manifest (this exe, the pinned extension only) and the per-user
    /// registry entries for Chrome, Edge, Brave, Vivaldi and Chromium. Null on success, else what went wrong.
    public static string? Connect()
    {
        try
        {
            WriteExtension();
            Directory.CreateDirectory(BridgeFolder);
            var manifest = new Dictionary<string, object>
            {
                ["name"] = HostName,
                ["description"] = "Purrch: YouTube videos from the browser in the pill",
                ["path"] = Environment.ProcessPath ?? "",
                ["type"] = "stdio",
                ["allowed_origins"] = new[] { $"chrome-extension://{ExtensionId}/" },
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var browser in BrowserKeys)
            {
                using var key = Registry.CurrentUser.CreateSubKey(HostKey(browser));
                key.SetValue("", ManifestPath);
            }
            Log.Write("Browser connected: extension and native messaging host registered");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Connect browser failed: {e.Message}");
            return e.Message;
        }
    }

    /// Removes the registry entries, the bridge manifest and the extension folder, and hangs up on browsers.
    public static void Disconnect()
    {
        foreach (var browser in BrowserKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(HostKey(browser), throwOnMissingSubKey: false); }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Write($"Disconnect {browser}: {e.Message}");
            }
        }
        foreach (var folder in new[] { BridgeFolder, ExtensionFolder })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Disconnect: {folder} not removed: {e.Message}");
            }
        }
        BrowserBridge.Shared.DropAll();
        Log.Write("Browser disconnected");
    }

    /// The extension's files ship inside Purrch.exe; this puts the current copy in the folder, replacing an
    /// older one (so Connect again also updates it; the browser picks it up on its next reload).
    private static void WriteExtension()
    {
        var assembly = Assembly.GetExecutingAssembly();
        Directory.CreateDirectory(ExtensionFolder);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var resource = assembly.GetManifestResourceStream(name);
            if (resource == null) continue;
            using var file = File.Create(Path.Combine(ExtensionFolder, name[ResourcePrefix.Length..]));
            resource.CopyTo(file);
        }
    }
}
