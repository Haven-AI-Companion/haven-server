using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AshServer.Data;
using AshServer.Models;

namespace AshServer.AI;

// ── Model descriptor ──────────────────────────────────────────────────────────

public record ModelDescriptor(
    string Id,          // "{backendId}:{modelName}"
    string Name,
    int BackendId,
    string BackendName,
    string BackendType
);

// ── Backend interface ─────────────────────────────────────────────────────────

public interface IAiBackend
{
    Task<List<string>> ListModels();
    IAsyncEnumerable<string> StreamChat(string model, List<ChatMessage> messages, CancellationToken ct = default);
    Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default);
}

// ── Ollama backend ────────────────────────────────────────────────────────────

public class OllamaBackend : IAiBackend
{
    private readonly string _baseUrl;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public OllamaBackend(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<List<string>> ListModels()
    {
        var resp = await Http.GetAsync($"{_baseUrl}/api/tags");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("models")
            .EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()!)
            .ToList();
    }

    public async IAsyncEnumerable<string> StreamChat(
        string model, List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = messages.Select(m => m.Images?.Count > 0
                ? (object)new { role = m.Role, content = m.Content, images = m.Images }
                : new { role = m.Role, content = m.Content }),
            stream = true
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }
            using (doc)
            {
                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var c))
                {
                    var text = c.GetString();
                    if (!string.IsNullOrEmpty(text)) yield return text;
                }
                if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
                    yield break;
            }
        }
    }

    public async Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            tools,
            stream = false
        });

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync($"{_baseUrl}/api/chat", content);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("message").Clone();
    }
}

// ── OpenAI-compatible backend ─────────────────────────────────────────────────

public class OpenAiCompatBackend : IAiBackend
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public OpenAiCompatBackend(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey ?? "none";
    }

    public async Task<List<string>> ListModels()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models");
        if (_apiKey != "none") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()!)
            .ToList();
    }

    public async IAsyncEnumerable<string> StreamChat(
        string model, List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (_apiKey != "none") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("data: "))
            {
                var data = line[6..].Trim();
                if (data == "[DONE]") yield break;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(data); }
                catch { continue; }
                using (doc)
                {
                    var text = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("delta")
                        .TryGetProperty("content", out var c) ? c.GetString() : null;
                    if (!string.IsNullOrEmpty(text)) yield return text;
                }
            }
        }
    }

    public Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
        => throw new NotSupportedException("Tool calling not yet implemented for OpenAI-compat backend");
}

// ── Anthropic Claude backend ──────────────────────────────────────────────────

public class AnthropicBackend : IAiBackend
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private const string AnthropicVersion = "2023-06-01";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public AnthropicBackend(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey ?? throw new ArgumentException("Anthropic backend requires an API key");
    }

    public async Task<List<string>> ListModels()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()!)
            .ToList();
    }

    public async IAsyncEnumerable<string> StreamChat(
        string model, List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var systemMsg  = messages.FirstOrDefault(m => m.Role == "system");
        var chatMsgs   = messages
            .Where(m => m.Role != "system")
            .Select(m => new { role = m.Role, content = m.Content })
            .ToList();

        var payload = new Dictionary<string, object>
        {
            ["model"]      = model,
            ["max_tokens"] = 8096,
            ["stream"]     = true,
            ["messages"]   = chatMsgs
        };
        if (systemMsg != null) payload["system"] = systemMsg.Content;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..].Trim();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                if (type == "message_stop") yield break;
                if (type == "content_block_delta" &&
                    doc.RootElement.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var text))
                {
                    var t = text.GetString();
                    if (!string.IsNullOrEmpty(t)) yield return t;
                }
            }
        }
    }

    public Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
        => throw new NotSupportedException("Tool calling not yet implemented for Anthropic backend");
}

// ── Google Gemini backend ─────────────────────────────────────────────────────

public class GeminiBackend : IAiBackend
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public GeminiBackend(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey ?? throw new ArgumentException("Gemini backend requires an API key");
    }

    public async Task<List<string>> ListModels()
    {
        var resp = await Http.GetAsync($"{_baseUrl}/v1beta/models?key={_apiKey}");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("models")
            .EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()!.Replace("models/", ""))
            .Where(n => n.Contains("gemini"))
            .ToList();
    }

    public async IAsyncEnumerable<string> StreamChat(
        string model, List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var systemMsg = messages.FirstOrDefault(m => m.Role == "system");
        var contents  = messages
            .Where(m => m.Role != "system")
            .Select(m => new
            {
                role  = m.Role == "assistant" ? "model" : "user",
                parts = new[] { new { text = m.Content } }
            })
            .ToList();

        var payload = new Dictionary<string, object> { ["contents"] = contents };
        if (systemMsg != null)
            payload["systemInstruction"] = new { parts = new[] { new { text = systemMsg.Content } } };

        var url = $"{_baseUrl}/v1beta/models/{model}:streamGenerateContent?key={_apiKey}&alt=sse";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..].Trim();
            if (string.IsNullOrEmpty(data)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }
            using (doc)
            {
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                    candidates.GetArrayLength() > 0 &&
                    candidates[0].TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var text))
                {
                    var t = text.GetString();
                    if (!string.IsNullOrEmpty(t)) yield return t;
                }
            }
        }
    }

    public Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
        => throw new NotSupportedException("Tool calling not yet implemented for Gemini backend");
}

// ── Backend manager ───────────────────────────────────────────────────────────

public class BackendManager
{
    private readonly Database _db;
    private List<(AiBackend Row, IAiBackend Instance)>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BackendManager(Database db)
    {
        _db = db;
    }

    public void Invalidate()
    {
        _cache = null;
    }

    private async Task EnsureLoaded()
    {
        if (_cache != null) return;
        await _lock.WaitAsync();
        try
        {
            if (_cache != null) return;
            var rows = await _db.GetEnabledBackends();
            _cache = rows.Select(r => (r, MakeBackend(r))).ToList();
        }
        finally { _lock.Release(); }
    }

    private static IAiBackend MakeBackend(AiBackend row) => row.Type switch
    {
        "openai" or "openai_compat" => new OpenAiCompatBackend(row.BaseUrl, row.ApiKey),
        "anthropic"                 => new AnthropicBackend(row.BaseUrl, row.ApiKey),
        "gemini"                    => new GeminiBackend(row.BaseUrl, row.ApiKey),
        _                           => new OllamaBackend(row.BaseUrl)
    };

    public static string MakeModelId(int backendId, string modelName) => $"{backendId}:{modelName}";

    public static (int? backendId, string modelName) ParseModelId(string modelId)
    {
        var idx = modelId.IndexOf(':');
        if (idx > 0 && int.TryParse(modelId[..idx], out var id))
            return (id, modelId[(idx + 1)..]);
        return (null, modelId);
    }

    public async Task<List<ModelDescriptor>> ListAllModels()
    {
        await EnsureLoaded();
        var results = new List<ModelDescriptor>();
        foreach (var (row, instance) in _cache!)
        {
            try
            {
                var names = await instance.ListModels();
                results.AddRange(names.Select(n => new ModelDescriptor(
                    MakeModelId(row.Id, n), n, row.Id, row.Name, row.Type)));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[backends] failed to list from '{row.Name}': {ex.Message}");
            }
        }

        // No backends configured — return empty list instead of silently using localhost.
        // Callers should prompt the user to configure a backend via the admin panel.
        return results;
    }

    public async Task<(IAiBackend backend, string modelName)> Resolve(string modelId)
    {
        await EnsureLoaded();
        var (backendId, modelName) = ParseModelId(modelId);

        if (backendId.HasValue)
        {
            var entry = _cache!.FirstOrDefault(e => e.Row.Id == backendId.Value);
            if (entry != default) return (entry.Instance, modelName);
        }

        // Dynamically find if any backend explicitly supports/advertises this modelName
        if (!string.IsNullOrEmpty(modelId))
        {
            foreach (var (row, instance) in _cache!)
            {
                try
                {
                    var models = await instance.ListModels();
                    if (models.Any(m => string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase)))
                        return (instance, modelId);
                }
                catch { /* Ignore unreachable/non-responsive backends and continue scanning */ }
            }
        }

        // Fallback: first available configured backend
        if (_cache!.Count > 0) return (_cache![0].Instance, modelId);

        throw new InvalidOperationException(
            "No AI backends are configured. Add a backend via the admin panel (/admin/backends).");
    }

    public async IAsyncEnumerable<string> StreamChat(
        string modelId, List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (backend, modelName) = await Resolve(modelId);
        await foreach (var token in backend.StreamChat(modelName, messages).WithCancellation(ct))
            yield return token;
    }
}
