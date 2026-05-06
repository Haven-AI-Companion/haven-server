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
        "openai" => new OpenAiCompatBackend(row.BaseUrl, row.ApiKey),
        _ => new OllamaBackend(row.BaseUrl)
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

        // Zero-config fallback
        if (results.Count == 0)
        {
            try
            {
                var fallback = new OllamaBackend("http://localhost:11434");
                var names = await fallback.ListModels();
                results.AddRange(names.Select(n => new ModelDescriptor(n, n, -1, "Local (auto)", "ollama")));
            }
            catch { }
        }

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

        // Fallback: first Ollama backend
        var ollama = _cache!.FirstOrDefault(e => e.Row.Type == "ollama");
        if (ollama != default) return (ollama.Instance, modelId);

        // Any backend
        if (_cache!.Count > 0) return (_cache![0].Instance, modelId);

        // Absolute fallback
        return (new OllamaBackend("http://localhost:11434"), modelId);
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
