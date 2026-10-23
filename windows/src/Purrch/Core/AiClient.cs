using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Purrch.Core;

/// Local is a model running on this PC (Ollama or Foundry Local), the Windows stand-in for the Mac's Apple on-device model.
public enum AiProvider { Claude, OpenAI, Gemini, Local }

public static class AiProviderInfo
{
    public static string Title(this AiProvider p) => p switch
    {
        AiProvider.OpenAI => "OpenAI",
        AiProvider.Gemini => "Gemini",
        AiProvider.Local => "Local (on-device)",
        _ => "Claude",
    };

    /// For tight capsules: "Local" rather than "Local (on-device)".
    public static string ShortTitle(this AiProvider p) => p == AiProvider.Local ? "Local" : p.Title();

    /// Credential Manager entry for the provider's key. Local has none.
    public static string SecretName(this AiProvider p) => p switch
    {
        AiProvider.OpenAI => "openai",
        AiProvider.Gemini => "gemini",
        AiProvider.Local => "",
        _ => "anthropic",
    };

    public static bool NeedsKey(this AiProvider p) => p != AiProvider.Local;

    /// True when the provider can be asked: its key is saved, or it needs none (Local).
    public static bool HasKey(this AiProvider p) => !p.NeedsKey() || SecretStore.Get(p.SecretName()).Length > 0;
}

/// One turn of a conversation.
public sealed record ChatTurn(bool FromUser, string Text);

/// A failure worth showing to the user as is ("Your Claude key was rejected").
public sealed class AiException(string message) : Exception(message);

/// Sends a prompt (with optional history) to Claude, OpenAI or Gemini using the user's own key,
/// or to a local model through its OpenAI-compatible endpoint.
public static class AiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<string> CompleteAsync(AiProvider provider, string system, IReadOnlyList<ChatTurn> history,
                                                   string prompt, bool webSearch, CancellationToken cancel)
    {
        var turns = Alternating(history, prompt);
        if (provider == AiProvider.Local) return await AskLocal(system, turns, cancel);
        var key = SecretStore.Get(provider.SecretName());
        if (key.Length == 0) throw new AiException($"{provider.Title()} needs an API key. Add it in Settings → AI.");
        return provider switch
        {
            AiProvider.OpenAI => await AskChat("https://api.openai.com/v1/chat/completions", key, "gpt-4o-mini", "OpenAI",
                                               system, turns, cancel),
            AiProvider.Gemini => await AskGemini(key, system, turns, cancel),
            _ => await AskClaude(key, system, turns, webSearch, cancel),
        };
    }

    // MARK: - Claude

    private static async Task<string> AskClaude(string key, string system, IReadOnlyList<ChatTurn> turns, bool webSearch,
                                                CancellationToken cancel)
    {
        var client = new AnthropicClient { ApiKey = key };
        var messages = turns.Select(t => t.FromUser
            ? (BetaMessageParam)new BetaUserMessageParam { Content = t.Text }
            : new BetaAssistantMessageParam { Content = t.Text }).ToList();
        MessageCreateParams Parameters() => new()
        {
            Model = "claude-opus-5-5",
            MaxTokens = 16000,
            System = system,
            // Short, helpful answers don't need deep thinking; low effort keeps replies quick.
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            // If a safety check declines a request, let the API retry it on a suitable fallback model.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages = messages.ToList(),
            Tools = webSearch ? [new BetaWebSearchTool20260209 { MaxUses = 3 }] : null,
        };

        var text = "";
        // A long web search can pause the turn; sending the partial answer back lets Claude carry on from there.
        for (var round = 0; round < 4; round++)
        {
            var response = await CreateClaude(client, Parameters(), cancel);
            text += string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            if (response.StopReason == "refusal")
                return text.Length == 0 ? "Claude declined this request. Try rephrasing it." : text + "\n\nClaude declined to continue this request.";
            if (response.StopReason != "pause_turn") return text;
            messages.Add(new BetaAssistantMessageParam { Content = AsParams(response.Content) });
        }
        return text;
    }

    /// The SDK has no response-to-request conversion; its JSON converters turn each block (thinking,
    /// server tool use, search results) into the matching param.
    private static List<BetaContentBlockParam> AsParams(IReadOnlyList<BetaContentBlock> content) =>
        JsonSerializer.Deserialize<List<BetaContentBlockParam>>(JsonSerializer.Serialize(content))
        ?? throw new AiException("Unexpected reply from Claude.");

    private static async Task<BetaMessage> CreateClaude(AnthropicClient client, MessageCreateParams parameters,
                                                       CancellationToken cancel)
    {
        try
        {
            return await client.Beta.Messages.Create(parameters, cancel);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new AiException("Claude rejected the API key. Check it in Settings → AI.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new AiException("Claude is rate limiting this key. Wait a moment and try again.");
        }
        catch (AnthropicBadRequestException e)
        {
            throw new AiException($"Claude couldn't take this request: {e.Message}");
        }
        catch (Anthropic5xxException)
        {
            throw new AiException("Claude is having trouble right now. Try again in a minute.");
        }
        catch (AnthropicApiException e)
        {
            throw new AiException($"Claude error: {e.Message}");
        }
        catch (AnthropicIOException)
        {
            throw new AiException("Couldn't reach Claude. Check your internet connection.");
        }
    }

    // MARK: - OpenAI and Local

    /// OpenAI's chat completions, which Ollama and Foundry Local speak too. Local servers take no key.
    private static async Task<string> AskChat(string url, string? key, string model, string name, string system,
                                              IReadOnlyList<ChatTurn> turns, CancellationToken cancel, bool local = false)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var t in turns) messages.Add(new JsonObject { ["role"] = t.FromUser ? "user" : "assistant", ["content"] = t.Text });
        var body = new JsonObject { ["model"] = model, ["messages"] = messages };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var json = await Send(request, name, cancel, local ? model : null);
        return json["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new AiException($"Unexpected reply from {name}.");
    }

    private static async Task<string> AskLocal(string system, IReadOnlyList<ChatTurn> turns, CancellationToken cancel)
    {
        var endpoint = LocalAi.Endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            throw new AiException("The local endpoint in Settings → AI isn't a valid address.");
        var model = AppSettings.Shared.LocalModel.Trim();
        // No model picked: use the first one the server has, as the status line in Settings says.
        if (model.Length == 0) model = (await LocalAi.ProbeAsync(cancel)).Models.FirstOrDefault() ?? LocalAi.DefaultModel;
        return await AskChat($"{endpoint}/v1/chat/completions", null, model, "The local model", system, turns, cancel, local: true);
    }

    // MARK: - Gemini

    private static async Task<string> AskGemini(string key, string system, IReadOnlyList<ChatTurn> turns, CancellationToken cancel)
    {
        var contents = new JsonArray();
        foreach (var t in turns)
        {
            contents.Add(new JsonObject
            {
                ["role"] = t.FromUser ? "user" : "model",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = t.Text } },
            });
        }
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } },
            ["contents"] = contents,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent");
        // The key travels in a header rather than the URL so it never appears in error strings.
        request.Headers.Add("x-goog-api-key", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var json = await Send(request, "Gemini", cancel);

        var first = json["candidates"]?[0] ?? throw new AiException("Gemini returned no answer.");
        var text = string.Concat((first["content"]?["parts"]?.AsArray() ?? new JsonArray())
            .Select(p => p?["text"]?.GetValue<string>() ?? ""));
        var reason = first["finishReason"]?.GetValue<string>();
        if (text.Length == 0 && reason is { } r && r != "STOP")
            return $"Gemini stopped without an answer ({r.ToLowerInvariant()}).";
        return text;
    }

    /// Sends the request and returns its JSON. <paramref name="localModel"/> is set for Local, where failing to
    /// connect means the server isn't running and a 404 means the model isn't pulled.
    private static async Task<JsonNode> Send(HttpRequestMessage request, string name, CancellationToken cancel,
                                             string? localModel = null)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancel);
        }
        catch (HttpRequestException)
        {
            throw new AiException(localModel != null ? LocalAi.NotRunning : $"Couldn't reach {name}. Check your internet connection.");
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new AiException($"{name} took too long to answer. Try again.");
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(cancel);
            JsonNode? json = null;
            try { json = JsonNode.Parse(raw); }
            catch (JsonException) { }

            if (!response.IsSuccessStatusCode)
            {
                var message = json?["error"]?["message"]?.GetValue<string>();
                throw new AiException((int)response.StatusCode switch
                {
                    404 when localModel != null => LocalAi.NotPulled(localModel),
                    401 or 403 => $"{name} rejected the API key. Check it in Settings → AI.",
                    429 => $"{name} is rate limiting this key. Wait a moment and try again.",
                    >= 500 => $"{name} is having trouble right now. Try again in a minute.",
                    _ => $"{name} error: {message ?? response.ReasonPhrase}",
                });
            }
            return json ?? throw new AiException($"Unexpected reply from {name}.");
        }
    }

    /// History as strictly alternating user/assistant turns that start with the user, plus the new prompt.
    private static List<ChatTurn> Alternating(IReadOnlyList<ChatTurn> history, string prompt)
    {
        var result = new List<ChatTurn>();
        foreach (var turn in history.Append(new ChatTurn(true, prompt)))
        {
            if (result.Count == 0 && !turn.FromUser) continue;
            if (result.Count > 0 && result[^1].FromUser == turn.FromUser)
                result[^1] = result[^1] with { Text = result[^1].Text + "\n\n" + turn.Text };
            else
                result.Add(turn);
        }
        return result;
    }
}
