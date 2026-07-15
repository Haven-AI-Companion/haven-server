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
    private readonly IConfiguration _config;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OllamaBackend(string baseUrl, IConfiguration config)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _config = config;
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
        await _lock.WaitAsync(ct);
        try
        {
            var temp = _config.GetValue<float>("ai:temperature", 0.7f);
            var topP = _config.GetValue<float>("ai:top_p", 0.9f);
            var freqPenalty = _config.GetValue<float>("ai:frequency_penalty", 0.0f);
            var presPenalty = _config.GetValue<float>("ai:presence_penalty", 0.0f);

            var payload = JsonSerializer.Serialize(new
            {
                model,
                messages = messages.Select(m => m.Images?.Count > 0
                    ? (object)new { role = m.Role, content = m.Content, images = m.Images }
                    : new { role = m.Role, content = m.Content }),
                stream = true,
                options = new
                {
                    stop = BackendManager.GetStopSequences(messages),
                    num_predict = 4096,
                    temperature = temp,
                    top_p = topP,
                    frequency_penalty = freqPenalty,
                    presence_penalty = presPenalty
                }
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
        finally
        {
            _lock.Release();
        }
    }

    public async Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var temp = _config.GetValue<float>("ai:temperature", 0.7f);
            var topP = _config.GetValue<float>("ai:top_p", 0.9f);
            var freqPenalty = _config.GetValue<float>("ai:frequency_penalty", 0.0f);
            var presPenalty = _config.GetValue<float>("ai:presence_penalty", 0.0f);

            var payload = JsonSerializer.Serialize(new
            {
                model,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                tools,
                stream = false,
                options = new
                {
                    stop = BackendManager.GetStopSequences(messages),
                    num_predict = 4096,
                    temperature = temp,
                    top_p = topP,
                    frequency_penalty = freqPenalty,
                    presence_penalty = presPenalty
                }
            });

            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync($"{_baseUrl}/api/chat", content);
            resp.EnsureSuccessStatusCode();
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("message").Clone();
        }
        finally
        {
            _lock.Release();
        }
    }
}

// ── OpenAI-compatible backend ─────────────────────────────────────────────────

public class OpenAiCompatBackend : IAiBackend
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly IConfiguration _config;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OpenAiCompatBackend(string baseUrl, string? apiKey, IConfiguration config)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey ?? "none";
        _config = config;
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
        await _lock.WaitAsync(ct);
        try
        {
            var temp = _config.GetValue<float>("ai:temperature", 0.7f);
            var topP = _config.GetValue<float>("ai:top_p", 0.9f);
            var freqPenalty = _config.GetValue<float>("ai:frequency_penalty", 0.0f);
            var presPenalty = _config.GetValue<float>("ai:presence_penalty", 0.0f);

            var payload = JsonSerializer.Serialize(new
            {
                model = string.IsNullOrEmpty(model) ? "model" : model,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                stream = true,
                stop = BackendManager.GetStopSequences(messages),
                max_tokens = 4096,
                temperature = temp,
                top_p = topP,
                frequency_penalty = freqPenalty,
                presence_penalty = presPenalty
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
            bool inReasoning = false;
            while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("data: "))
                {
                    var data = line[6..].Trim();
                    if (data == "[DONE]")
                    {
                        if (inReasoning)
                        {
                            yield return "</thought>\n";
                        }
                        yield break;
                    }
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(data); }
                    catch { continue; }
                    using (doc)
                    {
                        var delta = doc.RootElement
                            .GetProperty("choices")[0]
                            .GetProperty("delta");

                        string? reasoning = delta.TryGetProperty("reasoning_content", out var r) ? r.GetString() : null;
                        string? content = delta.TryGetProperty("content", out var c) ? c.GetString() : null;

                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            if (!inReasoning)
                            {
                                inReasoning = true;
                                yield return "<thought>" + reasoning;
                            }
                            else
                            {
                                yield return reasoning;
                            }
                        }
                        else if (!string.IsNullOrEmpty(content))
                        {
                            if (inReasoning)
                            {
                                inReasoning = false;
                                if (content.TrimStart().StartsWith("<thought>"))
                                {
                                    yield return content;
                                }
                                else
                                {
                                    yield return "</thought>\n" + content;
                                }
                            }
                            else
                            {
                                yield return content;
                            }
                        }
                    }
                }
            }
            if (inReasoning)
            {
                yield return "</thought>\n";
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var temp = _config.GetValue<float>("ai:temperature", 0.7f);
            var topP = _config.GetValue<float>("ai:top_p", 0.9f);
            var freqPenalty = _config.GetValue<float>("ai:frequency_penalty", 0.0f);
            var presPenalty = _config.GetValue<float>("ai:presence_penalty", 0.0f);

            var payload = JsonSerializer.Serialize(new
            {
                model = string.IsNullOrEmpty(model) ? "model" : model,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                tools,
                stream = false,
                stop = BackendManager.GetStopSequences(messages),
                max_tokens = 4096,
                temperature = temp,
                top_p = topP,
                frequency_penalty = freqPenalty,
                presence_penalty = presPenalty
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (_apiKey != "none") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var jsonStr = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(jsonStr);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").Clone();
        }
        finally
        {
            _lock.Release();
        }
    }
}

// ── Anthropic Claude backend ──────────────────────────────────────────────────

public class AnthropicBackend : IAiBackend
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private const string AnthropicVersion = "2023-06-01";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

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

    public async Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
    {
        var systemMsg = messages.FirstOrDefault(m => m.Role == "system");
        var contents = new List<object>();
        
        foreach (var m in messages.Where(m => m.Role != "system"))
        {
            var role = m.Role == "assistant" ? "model" : "user";
            var text = m.Content;
            
            if (m.Role == "tool")
            {
                role = "user";
                text = $"[Tool Result: {m.Content}]";
            }
            else if (m.Role == "assistant" && string.IsNullOrWhiteSpace(text))
            {
                text = "[Executing tool calls...]";
            }
            
            contents.Add(new
            {
                role,
                parts = new[] { new { text } }
            });
        }

        var functionDeclarations = new List<object>();
        if (tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("function", out var func))
                {
                    var name = func.GetProperty("name").GetString();
                    var desc = func.TryGetProperty("description", out var d) ? d.GetString() : "";
                    var parameters = func.TryGetProperty("parameters", out var p) ? (object)p : null;
                    
                    var decl = new Dictionary<string, object>
                    {
                        ["name"] = name ?? "unknown",
                        ["description"] = desc ?? ""
                    };
                    if (parameters != null)
                    {
                        decl["parameters"] = parameters;
                    }
                    functionDeclarations.Add(decl);
                }
            }
        }

        var payload = new Dictionary<string, object>
        {
            ["contents"] = contents
        };

        if (systemMsg != null)
        {
            payload["systemInstruction"] = new
            {
                parts = new[] { new { text = systemMsg.Content } }
            };
        }

        if (functionDeclarations.Count > 0)
        {
            payload["tools"] = new[]
            {
                new { functionDeclarations }
            };
        }

        var url = $"{_baseUrl}/v1beta/models/{model}:generateContent?key={_apiKey}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var jsonStr = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(jsonStr);
        var root = doc.RootElement;

        var contentText = "";
        var toolCallsList = new List<object>();

        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var contentElement) &&
            contentElement.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var funcCall))
                {
                    var name = funcCall.GetProperty("name").GetString();
                    var args = funcCall.GetProperty("args").Clone();
                    
                    toolCallsList.Add(new
                    {
                        id = $"call_{Guid.NewGuid().ToString("N")[..8]}",
                        type = "function",
                        function = new
                        {
                            name,
                            arguments = JsonSerializer.Serialize(args)
                        }
                    });
                }
                else if (part.TryGetProperty("text", out var textEl))
                {
                    contentText += textEl.GetString();
                }
            }
        }

        var resultObj = new Dictionary<string, object>
        {
            ["role"] = "assistant",
            ["content"] = contentText
        };
        
        if (toolCallsList.Count > 0)
        {
            resultObj["tool_calls"] = toolCallsList;
        }

        var serialized = JsonSerializer.Serialize(resultObj);
        return JsonDocument.Parse(serialized).RootElement.Clone();
    }
}

// ── Backend manager ───────────────────────────────────────────────────────────

public class BackendManager
{
    private readonly Database _db;
    private readonly GridManager _grid;
    private readonly IConfiguration _config;
    private List<(AiBackend Row, IAiBackend Instance)>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BackendManager(Database db, GridManager grid, IConfiguration config)
    {
        _db = db;
        _grid = grid;
        _config = config;
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

    private IAiBackend MakeBackend(AiBackend row) => row.Type switch
    {
        "openai" or "openai_compat" => new OpenAiCompatBackend(row.BaseUrl, row.ApiKey, _config),
        "anthropic"                 => new AnthropicBackend(row.BaseUrl, row.ApiKey),
        "gemini"                    => new GeminiBackend(row.BaseUrl, row.ApiKey),
        _                           => new OllamaBackend(row.BaseUrl, _config)
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
                var listTask = instance.ListModels();
                if (await Task.WhenAny(listTask, Task.Delay(2000)) == listTask)
                {
                    var names = await listTask;
                    results.AddRange(names.Select(n => new ModelDescriptor(
                        MakeModelId(row.Id, n), n, row.Id, row.Name, row.Type)));
                }
                else
                {
                    Console.Error.WriteLine($"[backends] Timeout (2s) listing models from '{row.Name}' ({row.BaseUrl}). Skipping.");
                }
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
        if (string.IsNullOrEmpty(modelId) || modelId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            var optimalWorker = _grid.GetOptimalWorker();
            if (optimalWorker != null)
            {
                Console.WriteLine($"[grid-router] Offloading inference request to worker node '{optimalWorker.Name}' ({optimalWorker.Id[..4]}).");
                return (new GridWorkerBackend(_grid, optimalWorker), "default");
            }
        }

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
                    var listTask = instance.ListModels();
                    if (await Task.WhenAny(listTask, Task.Delay(2000)) == listTask)
                    {
                        var models = await listTask;
                        if (models.Any(m => string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase)))
                            return (instance, modelId);
                    }
                    else
                    {
                        Console.Error.WriteLine($"[backend-router] Warning: Timeout (2s) listing models for backend '{row.Name}' ({row.BaseUrl}). Skipping.");
                    }
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

    internal static List<string> GetStopSequences(List<ChatMessage> messages)
    {
        var stops = new List<string>
        {
            "<|im_end|>",
            "<|im_start|>",
            "<end_of_turn>",
            "<start_of_turn>",
            "<|eot_id|>",
            "\nuser",
            "\nassistant",
            "\nsystem",
            "\nUser:",
            "\nAssistant:",
            "\nSystem:",
            "\nuser:",
            "\nassistant:"
        };

        foreach (var msg in messages)
        {
            if (string.IsNullOrEmpty(msg.Content)) continue;

            // Pattern 1: User name prefix in chat lines, e.g. "Daniel: hey"
            var lines = msg.Content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx > 0 && colonIdx < 30) // limit name length to 30 chars
                {
                    var nameCandidate = trimmed.Substring(0, colonIdx).Trim();
                    // Verify it looks like a valid name (alphanumeric/spaces/dashes)
                    if (System.Text.RegularExpressions.Regex.IsMatch(nameCandidate, @"^[a-zA-Z0-9_\-\s]+$"))
                    {
                        var stopSeq = nameCandidate + ":";
                        var stopSeqNl = "\n" + stopSeq;
                        if (!stops.Contains(stopSeqNl)) stops.Add(stopSeqNl);
                    }
                }
            }

            // Pattern 2: System prompt patterns, e.g. "The user's name is Daniel.", "You are speaking with Daniel.", "Roleplay as Nova.", "You are Nova."
            if (msg.Role == "system" || msg.Role == "user")
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(msg.Content, 
                    @"(?:The user's name is|speaking with|speaking to|address the user as|You are|Roleplay as)\s+([a-zA-Z0-9_\-]+)", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var name = match.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            var stopSeq = name + ":";
                            var stopSeqNl = "\n" + stopSeq;
                            if (!stops.Contains(stopSeqNl)) stops.Add(stopSeqNl);
                        }
                    }
                }
            }
        }

        return stops;
    }
}

public class GridWorkerBackend : IAiBackend
{
    private readonly GridManager _grid;
    private readonly ConnectedWorker _worker;

    public GridWorkerBackend(GridManager grid, ConnectedWorker worker)
    {
        _grid = grid;
        _worker = worker;
    }

    public Task<List<string>> ListModels()
    {
        return Task.FromResult(new List<string> { "default" });
    }

    public IAsyncEnumerable<string> StreamChat(string model, List<ChatMessage> messages, CancellationToken ct = default)
    {
        return _grid.StreamRemoteChatAsync(_worker, model, messages, ct);
    }

    public Task<JsonElement> ChatWithTools(string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
    {
        return _grid.ChatWithToolsRemoteAsync(_worker, model, messages, tools, ct);
    }
}
