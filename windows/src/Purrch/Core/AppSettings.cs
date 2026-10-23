using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Purrch.DesignSystem;

namespace Purrch.Core;

/// User preferences, saved as JSON in %APPDATA%\Purrch\settings.json. Secrets never go here.
public sealed class AppSettings : INotifyPropertyChanged
{
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
        public string AiProvider { get; set; } = "claude";
        public bool AssistantWebSearch { get; set; }
        public string LocalEndpoint { get; set; } = "http://localhost:11434";
        public string LocalModel { get; set; } = "";
        public string WeatherCity { get; set; } = "";
        public bool UseFahrenheit { get; set; }
        public string RevenueCurrency { get; set; } = "usd";
        public string PlausibleSite { get; set; } = "";
        public string PlausibleHost { get; set; } = "https://plausible.io";
        public string MicrosoftClientId { get; set; } = "";
        public string GoogleClientId { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // Declared after Json: static fields are set in the order they are written, and Load reads with Json.
    public static AppSettings Shared { get; } = Load();

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

    /// Which model the Assistant (and, unless it picks its own, Translate) uses.
    public AiProvider AiProvider
    {
        get => Enum.TryParse<AiProvider>(_data.AiProvider, ignoreCase: true, out var p) ? p : AiProvider.Claude;
        set { if (AiProvider != value) { _data.AiProvider = value.ToString().ToLowerInvariant(); Changed(); } }
    }

    /// Lets Claude search the web when it needs fresh information.
    public bool AssistantWebSearch
    {
        get => _data.AssistantWebSearch;
        set { if (_data.AssistantWebSearch != value) { _data.AssistantWebSearch = value; Changed(); } }
    }

    /// Address of the Local provider's OpenAI-compatible server: Ollama by default, or Foundry Local.
    public string LocalEndpoint
    {
        get => _data.LocalEndpoint;
        set { if (_data.LocalEndpoint != value) { _data.LocalEndpoint = value; Changed(); } }
    }

    /// Model the Local provider asks; empty means the server's first model, else llama3.2.
    public string LocalModel
    {
        get => _data.LocalModel;
        set { if (_data.LocalModel != value) { _data.LocalModel = value; Changed(); } }
    }

    /// City for the Weather module; empty means use this PC's location.
    public string WeatherCity
    {
        get => _data.WeatherCity;
        set { if (_data.WeatherCity != value) { _data.WeatherCity = value; Changed(); } }
    }

    /// Weather in °F and mph instead of °C and km/h.
    public bool UseFahrenheit
    {
        get => _data.UseFahrenheit;
        set { if (_data.UseFahrenheit != value) { _data.UseFahrenheit = value; Changed(); } }
    }

    /// ISO code of the Stripe charges Revenue counts ("usd"); charges in other currencies are left out.
    public string RevenueCurrency
    {
        get => _data.RevenueCurrency;
        set { if (_data.RevenueCurrency != value) { _data.RevenueCurrency = value; Changed(); } }
    }

    /// Site domain as it is registered in Plausible ("example.com").
    public string PlausibleSite
    {
        get => _data.PlausibleSite;
        set { if (_data.PlausibleSite != value) { _data.PlausibleSite = value; Changed(); } }
    }

    /// Plausible server, for self-hosted installs; plausible.io by default.
    public string PlausibleHost
    {
        get => _data.PlausibleHost;
        set { if (_data.PlausibleHost != value) { _data.PlausibleHost = value; Changed(); } }
    }

    /// Application (client) ID of the user's own Azure app registration, for signing in to Microsoft in Calendar.
    public string MicrosoftClientId
    {
        get => _data.MicrosoftClientId;
        set { if (_data.MicrosoftClientId != value) { _data.MicrosoftClientId = value; Changed(); } }
    }

    /// Client ID of the user's own Google "Desktop app" OAuth client; its secret is in Credential Manager.
    public string GoogleClientId
    {
        get => _data.GoogleClientId;
        set { if (_data.GoogleClientId != value) { _data.GoogleClientId = value; Changed(); } }
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
