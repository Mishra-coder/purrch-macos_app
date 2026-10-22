using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Purrch.DesignSystem;

namespace Purrch.Core;

/// User preferences, saved as JSON in %APPDATA%\Purrch\settings.json. Secrets never go here.
public sealed class AppSettings : INotifyPropertyChanged
{
    public static AppSettings Shared { get; } = Load();

    /// What is written to disk. Enums are stored by name so the file stays readable and survives reordering.
    private sealed class Data
    {
        public string Theme { get; set; } = "minimal";
        public bool OpenOnHover { get; set; } = true;
        public bool HideInFullscreen { get; set; } = true;
        public bool ShowLiveActivities { get; set; } = true;
        public List<string> DisabledModules { get; set; } = new();
        public List<string> ModuleOrder { get; set; } = new();
        public string LastModule { get; set; } = "home";
        public bool HasCompletedOnboarding { get; set; }
        public bool LaunchAtStartupDefaulted { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Data _data;
    private ThemeId _theme;
    private List<ModuleId> _order;
    private HashSet<ModuleId> _disabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    private AppSettings(Data data)
    {
        _data = data;
        _theme = Enum.TryParse<ThemeId>(data.Theme, ignoreCase: true, out var t) ? t : ThemeId.Minimal;
        _disabled = data.DisabledModules.Select(ModuleInfo.FromKey).OfType<ModuleId>().ToHashSet();
        _disabled.Remove(ModuleId.Home);
        _order = CompleteOrder(data.ModuleOrder.Select(ModuleInfo.FromKey).OfType<ModuleId>());
    }

    public ThemeId ThemeId
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            _data.Theme = Key(value);
            Changed();
            OnPropertyChanged(nameof(Theme));
        }
    }

    public Theme Theme => Theme.Make(_theme);

    public bool OpenOnHover
    {
        get => _data.OpenOnHover;
        set { if (_data.OpenOnHover != value) { _data.OpenOnHover = value; Changed(); } }
    }

    public bool HideInFullscreen
    {
        get => _data.HideInFullscreen;
        set { if (_data.HideInFullscreen != value) { _data.HideInFullscreen = value; Changed(); } }
    }

    public bool ShowLiveActivities
    {
        get => _data.ShowLiveActivities;
        set { if (_data.ShowLiveActivities != value) { _data.ShowLiveActivities = value; Changed(); } }
    }

    public ModuleId LastModule
    {
        get => ModuleInfo.FromKey(_data.LastModule) ?? ModuleId.Home;
        set { if (LastModule != value) { _data.LastModule = value.Key(); Changed(); } }
    }

    public bool HasCompletedOnboarding
    {
        get => _data.HasCompletedOnboarding;
        set { if (_data.HasCompletedOnboarding != value) { _data.HasCompletedOnboarding = value; Changed(); } }
    }

    /// Lives in the registry, not settings.json, so Task Manager's Startup tab and this toggle agree.
    public bool LaunchAtStartup
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            if (LaunchAtStartup == value) return;
            StartupRegistration.Set(value);
            OnPropertyChanged(nameof(LaunchAtStartup));
        }
    }

    /// Once opened, Purrch should keep coming back after a restart, like other tray apps.
    /// Only the first launch turns it on, so switching it off later sticks.
    public void ApplyStartupDefault()
    {
#if DEBUG
        // Development builds run from bin\Debug; don't put that path in the Run key.
        return;
#else
        if (_data.LaunchAtStartupDefaulted)
        {
            StartupRegistration.RefreshPath();
            return;
        }
        _data.LaunchAtStartupDefaulted = true;
        Save();
        LaunchAtStartup = true;
#endif
    }

    /// Tab-bar order. Always contains every module once.
    public IReadOnlyList<ModuleId> ModuleOrder => _order;

    public IReadOnlyList<ModuleId> EnabledModules => _order.Where(m => !_disabled.Contains(m)).ToList();

    public bool IsEnabled(ModuleId module) => !_disabled.Contains(module);

    /// Home can't be turned off: the panel always needs somewhere to land.
    public void SetEnabled(ModuleId module, bool enabled)
    {
        if (module == ModuleId.Home) return;
        if (!(enabled ? _disabled.Remove(module) : _disabled.Add(module))) return;
        _data.DisabledModules = _disabled.Select(m => m.Key()).ToList();
        Changed(nameof(EnabledModules));
    }

    /// Saves a new order for the enabled modules; hidden ones keep their relative spots at the end.
    public void ReorderEnabled(IEnumerable<ModuleId> enabled) =>
        SetOrder(enabled.Concat(_order.Where(_disabled.Contains)));

    public void ResetModuleOrder() => SetOrder(ModuleInfo.All);

    private void SetOrder(IEnumerable<ModuleId> order)
    {
        _order = CompleteOrder(order);
        _data.ModuleOrder = _order.Select(m => m.Key()).ToList();
        Changed(nameof(ModuleOrder));
        OnPropertyChanged(nameof(EnabledModules));
    }

    /// De-duplicates and appends any modules missing from the order (e.g. ones added in an update).
    private static List<ModuleId> CompleteOrder(IEnumerable<ModuleId> order)
    {
        var unique = order.Distinct().ToList();
        unique.AddRange(ModuleInfo.All.Where(m => !unique.Contains(m)));
        return unique;
    }

    private static string Key(ThemeId id) => char.ToLowerInvariant(id.ToString()[0]) + id.ToString()[1..];

    private void Changed([CallerMemberName] string? name = null)
    {
        Save();
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(AppPaths.Settings), Json);
                if (data != null) return new AppSettings(data);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Settings unreadable, using defaults: {e.Message}");
        }
        return new AppSettings(new Data());
    }

    private void Save()
    {
        try
        {
            // Write next to the real file, then swap, so a crash mid-write can't leave half a file.
            var temp = AppPaths.Settings + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, Json));
            File.Move(temp, AppPaths.Settings, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Settings not saved: {e.Message}");
        }
    }
}
