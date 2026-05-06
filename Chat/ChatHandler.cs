using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AshServer.Agent;
using AshServer.AI;
using AshServer.Auth;
using AshServer.Data;
using AshServer.Models;
using AshServer.Personality;

namespace AshServer.Chat;

/// <summary>
/// Handles raw WebSocket connections for chat.
/// Protocol matches the Python ash-server frontend exactly.
/// </summary>
public class ChatHandler
{
    private static readonly ConcurrentDictionary<string, List<ChatMessage>> ConvCache = new();
    private const int MaxHistoryMessages = 40;

    private readonly Database _db;
    private readonly BackendManager _backends;
    private readonly PersonalityLoader _personality;
    private readonly IConfiguration _config;

    public ChatHandler(Database db, BackendManager backends, PersonalityLoader personality, IConfiguration config)
    {
        _db = db;
        _backends = backends;
        _personality = personality;
        _config = config;
    }

    public async Task Handle(HttpContext context, WebSocket ws, int userId, string username)
    {
        string? conversationId = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        try
        {
            await SendJson(ws, new { type = "auth_ok", user = username }, cts.Token);

            var buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buf, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                JsonDocument doc;
                try { doc = JsonDocument.Parse(ms.ToArray()); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    var userMessage = "";
                    if (root.TryGetProperty("content", out var c)) userMessage = c.GetString()?.Trim() ?? "";
                    else if (root.TryGetProperty("message", out var mg)) userMessage = mg.GetString()?.Trim() ?? "";
                    var modelId = (root.TryGetProperty("model", out var m) ? m.GetString() : null) ?? _config["DefaultModel"] ?? "";
                    var agentMode = root.TryGetProperty("agent_mode", out var am) && am.GetBoolean();

                    // Images: base64 strings for vision models
                    List<string>? images = null;
                    if (root.TryGetProperty("images", out var imgsEl) && imgsEl.ValueKind == JsonValueKind.Array)
                    {
                        images = imgsEl.EnumerateArray()
                            .Select(i => i.GetString()).Where(s => !string.IsNullOrEmpty(s))
                            .Select(s => s!).ToList();
                        if (images.Count == 0) images = null;
                    }

                    if (root.TryGetProperty("conversation_id", out var cid) && !string.IsNullOrEmpty(cid.GetString()))
                    {
                        var reqId = cid.GetString()!;
                        if (reqId != conversationId)
                        {
                            var conv = await _db.GetConversation(reqId, userId);
                            if (conv != null)
                            {
                                conversationId = reqId;
                                if (!ConvCache.ContainsKey(conversationId))
                                    await LoadConvToCache(conversationId);
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(userMessage)) continue;

                    if (conversationId == null)
                    {
                        conversationId = await _db.CreateConversation(userId);
                        ConvCache[conversationId] = [];
                        await SendJson(ws, new { type = "conversation_id", content = conversationId }, cts.Token);
                    }

                    await _db.AddMessage(conversationId, "user", userMessage);

                    var history = ConvCache.GetOrAdd(conversationId, _ => []);
                    lock (history)
                    {
                        history.Add(new ChatMessage("user", userMessage, images));
                        if (history.Count > MaxHistoryMessages)
                            history.RemoveRange(0, history.Count - MaxHistoryMessages);
                    }

                    await SendJson(ws, new { type = "typing", content = true }, cts.Token);

                    var systemPrompt = _personality.GetSystemPrompt(username);
                    var messages = new List<ChatMessage> { new("system", systemPrompt) };
                    // Don't pass images on history replay — only the current message
                    lock (history)
                    {
                        foreach (var h in history)
                            messages.Add(h);
                    }

                    var responseText = "";
                    try
                    {
                        if (agentMode)
                        {
                            var (backend, modelName) = await _backends.Resolve(modelId);
                            var runner = new AgentRunner(backend, modelName);
                            await foreach (var evt in runner.Run(messages).WithCancellation(cts.Token))
                            {
                                switch (evt.Type)
                                {
                                    case "tool_call":
                                        await SendJson(ws, new { type = "agent_tool_call", tool = evt.ToolName, args = evt.ToolArgs, iteration = evt.Iteration }, cts.Token);
                                        break;
                                    case "tool_result":
                                        await SendJson(ws, new { type = "agent_tool_result", tool = evt.ToolName, result = evt.ToolResult, iteration = evt.Iteration }, cts.Token);
                                        break;
                                    case "final":
                                        responseText = evt.Content ?? "";
                                        await SendJson(ws, new { type = "token", content = responseText }, cts.Token);
                                        break;
                                    case "error":
                                        await SendJson(ws, new { type = "error", content = evt.Content }, cts.Token);
                                        break;
                                }
                            }
                        }
                        else
                        {
                            await foreach (var token in _backends.StreamChat(modelId, messages).WithCancellation(cts.Token))
                            {
                                responseText += token;
                                await SendJson(ws, new { type = "token", content = token }, cts.Token);
                            }
                        }

                        await SendJson(ws, new { type = "typing", content = false }, cts.Token);
                        await SendJson(ws, new { type = "done" }, cts.Token);

                        if (!string.IsNullOrEmpty(responseText))
                        {
                            await _db.AddMessage(conversationId, "assistant", responseText);
                            lock (history) { history.Add(new ChatMessage("assistant", responseText)); }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        await SendJson(ws, new { type = "error", content = ex.Message }, cts.Token);
                        await SendJson(ws, new { type = "typing", content = false }, cts.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    private async Task LoadConvToCache(string conversationId)
    {
        var msgs = await _db.GetMessages(conversationId);
        ConvCache[conversationId] = msgs
            .Select(m => new ChatMessage(m.Role, m.Content))
            .TakeLast(MaxHistoryMessages)
            .ToList();
    }

    internal static async Task SendJson(WebSocket ws, object data, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        if (ws.State == WebSocketState.Open)
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
