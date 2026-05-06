using System.Runtime.CompilerServices;
using System.Text.Json;
using AshServer.AI;
using AshServer.Models;
using AshServer.Plugins;

namespace AshServer.Agent;

public record AgentEvent(
    string Type,            // tool_call | tool_result | final | error
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

    public AgentRunner(IAiBackend backend, string model, PluginManager? plugins = null)
    {
        _backend = backend;
        _model   = model;
        _plugins = plugins;
    }

    /// Merge built-in tool definitions with enabled plugin tools.
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

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(builtins));
    }

    private async Task<string> DispatchTool(string name, JsonElement args)
    {
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
                yield return new AgentEvent("final", Content: content, Iteration: iteration);
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

        // Hit cap — force final answer without tools
        working.Add(new ChatMessage("user",
            "[System: You have reached the maximum tool calls. Please give your best answer now.]"));

        JsonElement finalResponse = default;
        string? capError = null;
        try
        {
            finalResponse = await _backend.ChatWithTools(_model, working, JsonSerializer.Deserialize<JsonElement>("[]"));
        }
        catch (Exception ex) { capError = ex.Message; }

        if (capError != null)
        {
            yield return new AgentEvent("error", Content: $"Agent hit iteration cap: {capError}", Iteration: MaxIterations);
            yield break;
        }

        var finalContent = finalResponse.TryGetProperty("content", out var fc) ? fc.GetString()?.Trim() ?? "" : "";
        yield return new AgentEvent("final", Content: finalContent, Iteration: MaxIterations);
    }
}

