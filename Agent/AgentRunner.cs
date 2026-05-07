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

    public AgentRunner(IAiBackend backend, string model, PluginManager? plugins = null, McpManager? mcp = null)
    {
        _backend = backend;
        _model   = model;
        _plugins = plugins;
        _mcp     = mcp;
    }

    /// Merge built-in tool definitions with enabled plugin tools and MCP tools.
    private JsonElement BuildToolDefinitions()
    {
        var builtins = JsonSerializer.Deserialize<List<JsonElement>>(
            AgentTools.ToolDefinitions.GetRawText())!;

        if (_plugins != null)
        {
            var pluginTools = _plugins.GetToolDefinitions();
            if (pluginTools.ValueKind == JsonValueKind.Array)
            {
                // Skip builtin-type tools from plugin manifests (already in AgentTools)
                var external = pluginTools.EnumerateArray()
                    .Where(t =>
                    {
                        // exclude if it matches a builtin tool name
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
                    // MCP tools take precedence over builtins only if names collide;
                    // add if not already present
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
        // MCP tools first (external protocol servers)
        if (_mcp != null && _mcp.IsMcpTool(name))
            return await _mcp.ExecuteToolAsync(name, args);

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

        for (int iteration = 1; iteration <= MaxIterations; iteration++)
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

