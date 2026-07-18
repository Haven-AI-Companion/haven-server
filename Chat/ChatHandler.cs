using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using AshServer.Agent;
using AshServer.AI;
using AshServer.Auth;
using AshServer.Data;
using AshServer.Mcp;
using AshServer.Models;
using AshServer.Personality;
using AshServer.Plugins;

namespace AshServer.Chat;

/// <summary>
/// Handles raw WebSocket connections for chat.
/// Protocol matches the Python ash-server frontend exactly.
/// </summary>
public class ChatHandler
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(2);
    private const int MaxHistoryMessages = 40;

    public static readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, byte>> ActiveSockets = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> SocketsLocks = new();

    public static async Task BroadcastToConversation(string conversationId, object data)
    {
        if (ActiveSockets.TryGetValue(conversationId, out var dict))
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            foreach (var ws in dict.Keys)
            {
                if (ws.State == WebSocketState.Open)
                {
                    var wsLock = SocketsLocks.GetValue(ws, socket => new SemaphoreSlim(1, 1));
                    await wsLock.WaitAsync();
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                    catch {}
                    finally
                    {
                        wsLock.Release();
                    }
                }
            }
        }
    }

    private readonly Database _db;
    private readonly BackendManager _backends;
    private readonly PersonalityLoader _personality;
    private readonly IConfiguration _config;
    private readonly PluginManager _plugins;
    private readonly McpManager    _mcp;
    private readonly IMemoryCache  _convCache;
    private readonly ILogger<ChatHandler> _log;
    private readonly RagService _rag;

    public ChatHandler(Database db, BackendManager backends, PersonalityLoader personality,
        IConfiguration config, PluginManager plugins, McpManager mcp, IMemoryCache convCache,
        ILogger<ChatHandler> log, RagService rag)
    {
        _db = db;
        _backends = backends;
        _personality = personality;
        _config = config;
        _plugins = plugins;
        _mcp = mcp;
        _convCache = convCache;
        _log = log;
        _rag = rag;
    }

    public async Task Handle(HttpContext context, WebSocket ws, int userId, string username, bool isAdmin = false, HashSet<string>? permissions = null)
    {
        string? conversationId = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var sendLock = new System.Threading.SemaphoreSlim(1, 1);

        async Task SafeSend(object data, CancellationToken token)
        {
            await sendLock.WaitAsync(token);
            try
            {
                await SendJson(ws, data, token);
            }
            finally
            {
                sendLock.Release();
            }
        }

        async Task TrySend(object data, CancellationToken token)
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await SafeSend(data, token);
                }
                catch {}
            }
        }

        // Permission helper — admins bypass all checks; deny-by-default when permissions unknown
        bool HasPerm(string perm) => isAdmin || (permissions?.Contains(perm) ?? false);

        try
        {
            // Gate: api_access — if user has no chat access, reject immediately
            if (!HasPerm(AshServer.Auth.Permissions.ApiAccess))
            {
                await SafeSend(new { type = "error", content = "Your account does not have chat access. Contact an administrator." }, cts.Token);
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "forbidden", cts.Token);
                return;
            }

            await SafeSend(new { type = "auth_ok", user = username }, cts.Token);

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

                    var customSystemPrompt = "";
                    if (userMessage.StartsWith("You are ") && userMessage.Contains("\n\n"))
                    {
                        int lastDoubleNewline = userMessage.LastIndexOf("\n\n");
                        if (lastDoubleNewline > 0)
                        {
                            customSystemPrompt = userMessage.Substring(0, lastDoubleNewline).Trim();
                            userMessage = userMessage.Substring(lastDoubleNewline + 2).Trim();
                        }
                    }

                    // Gate: agent_mode
                    var agentMode = root.TryGetProperty("agent_mode", out var am) && am.GetBoolean();
                    if (agentMode && !HasPerm(AshServer.Auth.Permissions.AgentMode))
                    {
                        agentMode = false;
                        await SafeSend(new { type = "warning", content = "Agent mode is not available for your account." }, cts.Token);
                    }

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
                                if (!_convCache.TryGetValue(conversationId, out List<ChatMessage>? _))
                                    await LoadConvToCache(conversationId);
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(userMessage)) continue;

                    if (conversationId == null)
                    {
                        conversationId = await _db.CreateConversation(userId);
                        _convCache.Set(conversationId, new List<ChatMessage>(), CacheTtl);
                        await SafeSend(new { type = "conversation_id", content = conversationId }, cts.Token);
                    }

                    if (conversationId == null)
                    {
                        _log.LogError("[chat-handler] Conversation ID could not be loaded or created.");
                        continue;
                    }

                    var dict = ActiveSockets.GetOrAdd(conversationId, _ => new ConcurrentDictionary<WebSocket, byte>());
                    dict.TryAdd(ws, 0);

                    try
                    {
                        await _db.AddMessage(conversationId, "user", userMessage);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                    {
                        // Conversation was deleted mid-session — create a fresh one and retry
                        conversationId = await _db.CreateConversation(userId);
                        _convCache.Remove(conversationId);
                        _convCache.Set(conversationId, new List<ChatMessage>(), CacheTtl);
                        await SafeSend(new { type = "conversation_id", content = conversationId }, cts.Token);
                        await _db.AddMessage(conversationId, "user", userMessage);
                    }

                    var history = _convCache.GetOrCreate(conversationId, e =>
                    {
                        e.SlidingExpiration = CacheTtl;
                        return new List<ChatMessage>();
                    })!;
                    lock (history)
                    {
                        history.Add(new ChatMessage("user", userMessage, images));
                        if (history.Count > MaxHistoryMessages)
                            history.RemoveRange(0, history.Count - MaxHistoryMessages);
                    }

                    await TrySend(new { type = "typing", content = true }, cts.Token);

                    var activeUser = await _db.GetUserById(userId);
                    var systemPrompt = !string.IsNullOrEmpty(customSystemPrompt)
                        ? customSystemPrompt
                        : _personality.GetSystemPrompt(username, activeUser?.DisplayName, activeUser?.Gender);
                    var messages = new List<ChatMessage> { new("system", systemPrompt) };
                    // Don't pass images on history replay — only the current message
                    lock (history)
                    {
                        foreach (var h in history)
                            messages.Add(h);
                    }

                    var responseText = "";

                    // Start a periodic background typing keep-alive task to prevent Kestrel/client timeout
                    using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    var keepAliveTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!timerCts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(5000, timerCts.Token);
                                if (!timerCts.Token.IsCancellationRequested)
                                {
                                    await TrySend(new { type = "typing", content = true }, timerCts.Token);
                                }
                            }
                        }
                        catch (OperationCanceledException) {}
                        catch (Exception) {}
                    });

                    using var generationCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    var genToken = generationCts.Token;

                    try
                    {
                        if (agentMode)
                        {
                            var (backend, modelName) = await _backends.Resolve(modelId);
                            var conversation = await _db.GetConversation(conversationId, userId);
                            var companionName = string.IsNullOrEmpty(conversation?.CompanionId) ? (_personality.AiName ?? "Default") : conversation.CompanionId;
                            var runner = new AgentRunner(backend, modelName, _plugins, _mcp, _rag, conversationId: conversationId, companionName: companionName, userId: userId);
                            await foreach (var evt in runner.Run(messages).WithCancellation(genToken))
                            {
                                switch (evt.Type)
                                {
                                    case "stream_token":
                                        responseText += evt.Content ?? "";
                                        await TrySend(new { type = "token", content = evt.Content }, cts.Token);
                                        break;
                                    case "tool_call":
                                        await TrySend(new { type = "agent_tool_call", tool = evt.ToolName, args = evt.ToolArgs, iteration = evt.Iteration }, cts.Token);
                                        break;
                                    case "tool_result":
                                        await TrySend(new { type = "agent_tool_result", tool = evt.ToolName, result = evt.ToolResult, iteration = evt.Iteration }, cts.Token);
                                        if (evt.ToolName == "generate_portrait" && !string.IsNullOrEmpty(evt.ToolResult) && evt.ToolResult.StartsWith("/uploads/"))
                                        {
                                            var imgMarkdown = $"\n\n![Generated Portrait]({evt.ToolResult})";
                                            responseText += imgMarkdown;
                                            await TrySend(new { type = "token", content = imgMarkdown }, cts.Token);
                                        }
                                        break;
                                    case "final":
                                        // responseText already accumulated from stream_token events
                                        break;
                                    case "error":
                                        await TrySend(new { type = "error", content = evt.Content }, cts.Token);
                                        break;
                                }
                            }
                        }
                        else
                        {
                            await foreach (var token in _backends.StreamChat(modelId, messages).WithCancellation(genToken))
                            {
                                responseText += token;
                                await TrySend(new { type = "token", content = token }, cts.Token);
                            }
                        }

                        await TrySend(new { type = "typing", content = false }, cts.Token);
                        await TrySend(new { type = "done" }, cts.Token);

                        if (!string.IsNullOrEmpty(responseText))
                        {
                            await _db.AddMessage(conversationId, "assistant", responseText, _personality.AiName);
                            lock (history) { history.Add(new ChatMessage("assistant", responseText)); }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (genToken.IsCancellationRequested) break;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Configuration errors (e.g. no backend configured) — surface the message directly.
                        _log.LogWarning("[chat] Configuration error for user {User}: {Message}", username, ex.Message);
                        await TrySend(new { type = "error", content = ex.Message }, cts.Token);
                        await TrySend(new { type = "typing", content = false }, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "[chat] Error processing message for user {User}", username);
                        await TrySend(new { type = "error", content = "An error occurred while processing your message." }, cts.Token);
                        await TrySend(new { type = "typing", content = false }, cts.Token);
                    }
                    finally
                    {
                        await timerCts.CancelAsync();
                        try { await keepAliveTask; } catch {}
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (conversationId != null && ActiveSockets.TryGetValue(conversationId, out var dict))
            {
                dict.TryRemove(ws, out _);
                if (dict.IsEmpty)
                {
                    ActiveSockets.TryRemove(conversationId, out _);
                }
            }
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    private async Task LoadConvToCache(string conversationId)
    {
        var msgs = await _db.GetMessages(conversationId);
        var history = msgs
            .Select(m => new ChatMessage(m.Role, m.Content))
            .TakeLast(MaxHistoryMessages)
            .ToList();
        _convCache.Set(conversationId, history, CacheTtl);
    }

    internal static async Task SendJson(WebSocket ws, object data, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        if (ws.State == WebSocketState.Open)
        {
            var wsLock = SocketsLocks.GetValue(ws, socket => new SemaphoreSlim(1, 1));
            await wsLock.WaitAsync(ct);
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
            }
            finally
            {
                wsLock.Release();
            }
        }
    }
}
