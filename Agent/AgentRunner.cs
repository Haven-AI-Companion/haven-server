using System.Runtime.CompilerServices;
using System.Text.Json;
using AshServer.AI;
using AshServer.Mcp;
using AshServer.Models;
using AshServer.Plugins;

namespace AshServer.Agent;

public record AgentEvent(
    string Type,            // tool_call | tool_result | stream_token | final | error
    string? ToolName = null,
    JsonElement? ToolArgs = null,
    string? ToolResult = null,
    string? Content = null,
    int Iteration = 0
);

public class AgentRunner
{
    private const int MaxIterations = 8;

    private readonly IAiBackend _backend;
    private readonly string _model;
    private readonly PluginManager? _plugins;
    private readonly McpManager?    _mcp;
    private readonly RagService?    _rag;
    private readonly int _maxIterations;
    private readonly string? _conversationId;
    private readonly string? _companionName;
    private readonly int? _userId;

    public AgentRunner(IAiBackend backend, string model, PluginManager? plugins = null, McpManager? mcp = null, RagService? rag = null, int maxIterations = MaxIterations, string? conversationId = null, string? companionName = null, int? userId = null)
    {
        _backend = backend;
        _model   = model;
        _plugins = plugins;
        _mcp     = mcp;
        _rag     = rag;
        _maxIterations = Math.Max(1, maxIterations);
        _conversationId = conversationId;
        _companionName = companionName;
        _userId = userId;
    }

    /// Merge built-in tool definitions with enabled plugin tools and MCP tools.
    private JsonElement BuildToolDefinitions()
    {
        var builtins = JsonSerializer.Deserialize<List<JsonElement>>(
            AgentTools.ToolDefinitions.GetRawText())!;

        if (_rag != null)
        {
            var searchDocTool = JsonSerializer.Deserialize<JsonElement>("""
                {
                  "type": "function",
                  "function": {
                    "name": "search_documents",
                    "description": "Search through your uploaded documents and files for relevant information using semantic search.",
                    "parameters": {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string", "description": "The search query" },
                        "filename": { "type": "string", "description": "Optional filename to restrict search to" }
                      },
                      "required": ["query"]
                    }
                  }
                }
                """);
            builtins.Add(searchDocTool);
        }

        if (_plugins != null)
        {
            var pluginTools = _plugins.GetToolDefinitions();
            if (pluginTools.ValueKind == JsonValueKind.Array)
            {
                // Skip builtin-type tools from plugin manifests (already in AgentTools)
                var external = pluginTools.EnumerateArray()
                    .Where(t =>
                    {
                        var name = t.TryGetProperty("function", out var f) &&
                                   f.TryGetProperty("name", out var n) ? n.GetString() : null;
                        return name != null && !AgentTools.IsBuiltinTool(name);
                    });
                builtins.AddRange(external);
            }
        }

        if (_mcp != null)
        {
            var mcpTools = _mcp.GetToolDefinitions();
            if (mcpTools.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in mcpTools.EnumerateArray())
                {
                    var name = t.TryGetProperty("function", out var f) &&
                               f.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name != null && !builtins.Any(b =>
                        b.TryGetProperty("function", out var bf) &&
                        bf.TryGetProperty("name", out var bn) &&
                        bn.GetString() == name))
                        builtins.Add(t);
                }
            }
        }

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(builtins));
    }

    private async Task<string> DispatchTool(string name, JsonElement args)
    {
        if (name == "search_documents" && _rag != null)
        {
            var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var filename = args.TryGetProperty("filename", out var f) ? f.GetString() : null;
            
            if (string.IsNullOrWhiteSpace(query)) return "No query provided.";
            
            var results = await _rag.SearchAsync(query, filename);
            if (results.Count == 0) return "No matching document snippets found.";
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Semantic search results for \"{query}\":\n");
            foreach (var r in results)
            {
                sb.AppendLine($"--- Snippet from {r.Filename} (Similarity: {r.Similarity:F2}) ---");
                sb.AppendLine(r.Content);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // MCP tools first (external protocol servers)
        if (_mcp != null && _mcp.IsMcpTool(name))
            return await _mcp.ExecuteToolAsync(name, args);

        if (name == "generate_portrait" || name == "generate_3d_avatar")
        {
            if (string.IsNullOrEmpty(_conversationId))
            {
                if (_plugins != null && _plugins.IsPluginTool(name))
                    return await _plugins.ExecuteTool(name, args);
                return await AgentTools.Execute(name, args);
            }

            var uuidStr = Guid.NewGuid().ToString("N");
            var ext = name == "generate_portrait" ? ".webp" : ".glb";
            var prefix = name == "generate_portrait" ? "gen_" : "avatar_";
            
            string targetFilename;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("group_name", out var gnEl) && gnEl.ValueKind == JsonValueKind.String)
            {
                var groupName = gnEl.GetString() ?? "DefaultGroup";
                foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                {
                    groupName = groupName.Replace(c, '_');
                }
                targetFilename = $"groups/{groupName}/{prefix}{uuidStr}{ext}";
            }
            else
            {
                var companion = string.IsNullOrEmpty(_companionName) ? "Default" : _companionName;
                var user = _userId?.ToString() ?? "0";
                foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                {
                    companion = companion.Replace(c, '_');
                }
                targetFilename = $"{companion}/{user}/{prefix}{uuidStr}{ext}";
            }
            var outputUrl = $"/uploads/{targetFilename}";

            var argsDict = new Dictionary<string, object>();
            if (args.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in args.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        argsDict[prop.Name] = prop.Value.GetString()!;
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                        argsDict[prop.Name] = prop.Value.GetDouble();
                    else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                        argsDict[prop.Name] = prop.Value.GetBoolean();
                    else
                        argsDict[prop.Name] = prop.Value;
                }
            }
            argsDict["target_filename"] = targetFilename;

            var updatedArgs = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(argsDict));

            // Fire and forget task to generate media in background
            _ = Task.Run(async () =>
            {
                await SdGenerationQueue.Semaphore.WaitAsync();
                try
                {
                    string result;
                    if (_plugins != null && _plugins.IsPluginTool(name))
                        result = await _plugins.ExecuteTool(name, updatedArgs);
                    else
                        result = await AgentTools.Execute(name, updatedArgs);

                    if (!string.IsNullOrEmpty(result) && result.StartsWith("/uploads/"))
                    {
                        await AshServer.Chat.ChatHandler.BroadcastToConversation(_conversationId, new
                        {
                            type = "media_ready",
                            url = result,
                            tool = name
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[async-tool] Error in background execution of {name}: {ex.Message}");
                }
                finally
                {
                    SdGenerationQueue.Semaphore.Release();
                }
            });

            return outputUrl;
        }

        // Plugin tools (non-builtin) take priority if PluginManager knows them
        if (_plugins != null && _plugins.IsPluginTool(name))
            return await _plugins.ExecuteTool(name, args);

        return await AgentTools.Execute(name, args);
    }

    public async IAsyncEnumerable<AgentEvent> Run(
        List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var working = new List<ChatMessage>(messages);

        for (int iteration = 1; iteration <= _maxIterations; iteration++)
        {
            // Separate the AI call so we can yield inside the error path
            JsonElement response = default;
            string? callError = null;
            try
            {
                response = await _backend.ChatWithTools(_model, working, BuildToolDefinitions());
            }
            catch (Exception ex)
            {
                callError = ex.Message;
            }

            if (callError != null)
            {
                yield return new AgentEvent("error", Content: callError, Iteration: iteration);
                yield break;
            }

            var hasToolCalls = response.TryGetProperty("tool_calls", out var toolCallsEl) &&
                               toolCallsEl.ValueKind == JsonValueKind.Array &&
                               toolCallsEl.GetArrayLength() > 0;

            var content = response.TryGetProperty("content", out var contentEl)
                ? contentEl.GetString()?.Trim() ?? ""
                : "";

            if (!hasToolCalls)
            {
                // Stream the final answer token-by-token for better UX
                await foreach (var token in _backend.StreamChat(_model, working, ct))
                    yield return new AgentEvent("stream_token", Content: token, Iteration: iteration);
                yield return new AgentEvent("final", Iteration: iteration);
                yield break;
            }

            if (!string.IsNullOrEmpty(content))
            {
                var formattedContent = content;
                if (!content.TrimStart().StartsWith("<thought", StringComparison.OrdinalIgnoreCase))
                {
                    formattedContent = $"<thought>{content}</thought>";
                }
                yield return new AgentEvent("stream_token", Content: formattedContent + "\n", Iteration: iteration);
            }

            working.Add(new ChatMessage("assistant", content));

            foreach (var tc in toolCallsEl.EnumerateArray())
            {
                if (ct.IsCancellationRequested) yield break;

                var func = tc.TryGetProperty("function", out var f) ? f : default;
                var name = func.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown";

                JsonElement args = default;
                if (func.TryGetProperty("arguments", out var argsEl))
                {
                    if (argsEl.ValueKind == JsonValueKind.String)
                    {
                        try { args = JsonDocument.Parse(argsEl.GetString()!).RootElement; }
                        catch { args = argsEl; }
                    }
                    else { args = argsEl; }
                }

                yield return new AgentEvent("tool_call", ToolName: name, ToolArgs: args, Iteration: iteration);

                string result;
                string? toolError = null;
                try { result = await DispatchTool(name, args); }
                catch (Exception ex) { result = ""; toolError = ex.Message; }

                if (toolError != null)
                {
                    yield return new AgentEvent("error", ToolName: name, Content: toolError, Iteration: iteration);
                    yield break;
                }

                yield return new AgentEvent("tool_result", ToolName: name, ToolResult: result, Iteration: iteration);
                working.Add(new ChatMessage("tool", result));
            }
        }

        // Hit cap — collect tokens first (can't yield inside try/catch), then stream
        working.Add(new ChatMessage("user",
            "[System: You have reached the maximum tool calls. Please give your best answer now.]"));

        var capTokens = new List<string>();
        string? capError = null;
        try
        {
            await foreach (var token in _backend.StreamChat(_model, working, ct))
                capTokens.Add(token);
        }
        catch (Exception ex) { capError = ex.Message; }

        if (capError != null)
        {
            yield return new AgentEvent("error", Content: $"Agent hit iteration cap: {capError}", Iteration: MaxIterations);
            yield break;
        }

        foreach (var token in capTokens)
            yield return new AgentEvent("stream_token", Content: token, Iteration: MaxIterations);

        yield return new AgentEvent("final", Iteration: MaxIterations);
    }
}

public static class SdGenerationQueue
{
    public static readonly System.Threading.SemaphoreSlim Semaphore = new(1, 1);
}

