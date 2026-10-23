using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Purrch.Core;

/// The "Local (on-device)" provider: an OpenAI-compatible server on this PC, such as Ollama or Foundry Local.
/// It needs no key, like the Mac app's Apple on-device model, and explains why it can't answer.
public static class LocalAi
{
    public const string DefaultEndpoint = "http://localhost:11434";
    /// Used when no model is set and the server lists none.
    public const string DefaultModel = "llama3.2";

    public const string NotRunning =
        "Ollama isn't running — install it from ollama.com and run `ollama pull llama3.2`, or pick another provider in Settings → AI.";

    public static string NotPulled(string model) => $"Model {model} isn't pulled — run `ollama pull {model}`.";

    /// Short timeout: this only asks a server on this PC what it has.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// What answered at the endpoint, and the models it has.
    public sealed record Status(bool Running, string Server, IReadOnlyList<string> Models);

    /// The endpoint from Settings without a trailing slash; the Ollama default when blank.
    public static string Endpoint
    {
        get
        {
            var endpoint = AppSettings.Shared.LocalEndpoint.Trim().TrimEnd('/');
            return endpoint.Length == 0 ? DefaultEndpoint : endpoint;
        }
    }

    /// Asks Ollama's /api/tags first, then the OpenAI-style /v1/models that Foundry Local serves.
    public static async Task<Status> ProbeAsync(CancellationToken cancel = default)
    {
        var endpoint = Endpoint;
        if (await Get($"{endpoint}/api/tags", cancel) is { } tags)
            return new Status(true, "Ollama", Names((tags as JsonObject)?["models"], "name"));
        if (await Get($"{endpoint}/v1/models", cancel) is { } models)
            return new Status(true, "The local server", Names((models as JsonObject)?["data"], "id"));
        return new Status(false, "", []);
    }

    /// The model a request will use: the one set in Settings, else the server's first, else llama3.2.
    public static string ModelFor(Status status)
    {
        var model = AppSettings.Shared.LocalModel.Trim();
        return model.Length > 0 ? model : status.Models.FirstOrDefault() ?? DefaultModel;
    }

    /// One line for Settings: running with N models, not running, or the chosen model missing.
    public static string Describe(Status status)
    {
        if (!status.Running) return "Ollama isn't running — install it from ollama.com and run `ollama pull llama3.2`.";
        var model = ModelFor(status);
        // Ollama names carry a tag; "llama3.2" means "llama3.2:latest".
        var has = status.Models.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)
                                         || m.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase));
        if (!has) return status.Server == "Ollama" ? NotPulled(model) : $"Model {model} isn't loaded.";
        var count = status.Models.Count;
        return $"{status.Server} is running · {count} model{(count == 1 ? "" : "s")}";
    }

    private static List<string> Names(JsonNode? list, string key) =>
        (list as JsonArray ?? new JsonArray())
            .Select(m => (m as JsonObject)?[key]?.GetValue<string>() ?? "")
            .Where(n => n.Length > 0)
            .ToList();

    /// The JSON at the address, or null when nothing (or something else) answers there.
    private static async Task<JsonNode?> Get(string url, CancellationToken cancel)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or UriFormatException or InvalidOperationException
                                       || (e is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            return null;
        }
    }
}
