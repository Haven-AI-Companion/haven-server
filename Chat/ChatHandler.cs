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

                    string? payloadCompanionName = null;
                    if (root.TryGetProperty("companion_name", out var cn) && !string.IsNullOrEmpty(cn.GetString()))
                    {
                        payloadCompanionName = cn.GetString();
                    }
                    string? payloadCompanionId = null;
                    if (root.TryGetProperty("companion_id", out var ci) && !string.IsNullOrEmpty(ci.GetString()))
                    {
                        payloadCompanionId = ci.GetString();
                    }

                    string? groupId = null;
                    if (root.TryGetProperty("group_id", out var gProp) && !string.IsNullOrEmpty(gProp.GetString()))
                    {
                        groupId = gProp.GetString();
                    }

                    if (!string.IsNullOrEmpty(groupId))
                    {
                        await HandleGroupChatMessage(ws, groupId, userMessage, modelId, payloadCompanionName, userId, username, SafeSend, TrySend, cts.Token);
                        continue;
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
                                if (string.IsNullOrEmpty(conv.CompanionId) && !string.IsNullOrEmpty(payloadCompanionId ?? payloadCompanionName))
                                {
                                    await _db.SetConversationCompanion(conversationId, payloadCompanionId ?? payloadCompanionName!);
                                }
                                if (!_convCache.TryGetValue(conversationId, out List<ChatMessage>? _))
                                    await LoadConvToCache(conversationId);
                            }
                            else
                            {
                                conversationId = await _db.GetOrCreateCompanionConversation(userId, payloadCompanionId ?? payloadCompanionName, payloadCompanionName, customId: reqId);
                                if (!_convCache.TryGetValue(conversationId, out List<ChatMessage>? _))
                                    await LoadConvToCache(conversationId);
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(userMessage)) continue;

                    if (conversationId == null)
                    {
                        conversationId = await _db.GetOrCreateCompanionConversation(userId, payloadCompanionId ?? payloadCompanionName, payloadCompanionName);
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

                    bool isRegenerate = userMessage == "[REGENERATE]";
                    int? regenMessageId = null;
                    var existingSwipes = new List<string>();

                    if (isRegenerate)
                    {
                        try
                        {
                            var dbMessages = await _db.GetMessages(conversationId);
                            var lastAssistantMsg = dbMessages.LastOrDefault(msg => msg.Role == "assistant");
                            if (lastAssistantMsg != null)
                            {
                                regenMessageId = lastAssistantMsg.Id;
                                var rawContent = lastAssistantMsg.Content;
                                if (rawContent.StartsWith("{\"swipes\":") && rawContent.EndsWith("}"))
                                {
                                    using var swipeDoc = JsonDocument.Parse(rawContent);
                                    var swipeRoot = swipeDoc.RootElement;
                                    existingSwipes = swipeRoot.GetProperty("swipes").EnumerateArray()
                                        .Select(s => s.GetString() ?? "").ToList();
                                }
                                else
                                {
                                    existingSwipes = new List<string> { rawContent };
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "[chat-handler] Failed to read swipes for regeneration");
                        }
                    }
                    else
                    {
                        try
                        {
                            await _db.AddMessage(conversationId, "user", userMessage);
                        }
                        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                        {
                            // Conversation was deleted mid-session — create a fresh one and retry
                            conversationId = await _db.GetOrCreateCompanionConversation(userId, payloadCompanionName);
                            _convCache.Remove(conversationId);
                            _convCache.Set(conversationId, new List<ChatMessage>(), CacheTtl);
                            await SafeSend(new { type = "conversation_id", content = conversationId }, cts.Token);
                            await _db.AddMessage(conversationId, "user", userMessage);
                        }
                    }

                    var history = _convCache.GetOrCreate(conversationId, e =>
                    {
                        e.SlidingExpiration = CacheTtl;
                        return new List<ChatMessage>();
                    })!;

                    if (isRegenerate)
                    {
                        lock (history)
                        {
                            history.Clear();
                        }
                        var dbMsgs = await _db.GetMessages(conversationId);
                        var msgList = dbMsgs.ToList();
                        if (regenMessageId.HasValue)
                        {
                            msgList = msgList.Where(msg => msg.Id != regenMessageId.Value).ToList();
                        }
                        foreach (var msg in msgList)
                        {
                            if (msg.Role == "user" || msg.Role == "assistant")
                            {
                                lock (history)
                                {
                                    history.Add(new ChatMessage(msg.Role, ExtractActiveSwipe(msg.Content)));
                                }
                            }
                        }
                    }
                    else
                    {
                        lock (history)
                        {
                            history.Add(new ChatMessage("user", userMessage, images));
                            if (history.Count > MaxHistoryMessages)
                                history.RemoveRange(0, history.Count - MaxHistoryMessages);
                        }
                    }

                    await TrySend(new { type = "typing", content = true }, cts.Token);

                    var conversation = await _db.GetConversation(conversationId, userId);
                    var companionName = string.IsNullOrEmpty(conversation?.CompanionId) ? (payloadCompanionName ?? _personality.AiName ?? "Default") : conversation.CompanionId;

                    var activeUser = await _db.GetUserById(userId);
                    var userGenderDirective = AshServer.Personality.PersonalityLoader.BuildUserGenderDirective(activeUser?.DisplayName ?? username, activeUser?.Gender);
                    var baseSystemPrompt = !string.IsNullOrEmpty(customSystemPrompt)
                        ? customSystemPrompt
                        : GetCompanionSystemPrompt(companionName, username, activeUser?.DisplayName, activeUser?.Gender, conversationId);

                    var systemPrompt = baseSystemPrompt.Contains("[STRICT USER PRONOUN & GENDER DIRECTIVE]")
                        ? baseSystemPrompt
                        : baseSystemPrompt + "\n" + userGenderDirective;
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
                            var lowerResponse = responseText.ToLowerInvariant();
                            bool shouldGenSelfie = responseText.Contains("<call>generate_portrait</call>");

                            if (shouldGenSelfie)
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var compName = companionName;
                                        var compClean = string.Concat(compName.Split(Path.GetInvalidFileNameChars())).Trim();
                                        var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
                                        var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
                                        var localDir = Path.Combine(baseDir, "local");
                                        var localFile = Path.Combine(localDir, $"{compClean.ToLowerInvariant()}.json");
                                        var baseFile = Path.Combine(baseDir, $"{compClean.ToLowerInvariant()}.json");
                                        var checkFile = File.Exists(localFile) ? localFile : (File.Exists(baseFile) ? baseFile : null);

                                        AshServer.Controllers.CompanionConfig? comp = null;
                                        if (checkFile != null)
                                        {
                                            var json = await File.ReadAllTextAsync(checkFile);
                                            comp = JsonSerializer.Deserialize<AshServer.Controllers.CompanionConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                        }

                                        var details = comp?.Description ?? comp?.Personality ?? "";
                                        var location = comp?.CurrentLocation ?? "";
                                        var outfit = comp?.CurrentOutfit ?? "";
                                        var mood = comp?.CurrentMood ?? "";
                                        var clothing = comp?.ClothingState ?? "";

                                        var sdPrompt = $"digital art portrait of {compName}, highly detailed";
                                        if (!string.IsNullOrWhiteSpace(details)) sdPrompt += $", {details}";
                                        if (!string.IsNullOrWhiteSpace(location)) sdPrompt += $", at/in {location}";
                                        if (!string.IsNullOrWhiteSpace(outfit)) sdPrompt += $", wearing {outfit}";
                                        if (!string.IsNullOrWhiteSpace(mood)) sdPrompt += $", {mood} expression";
                                        if (!string.IsNullOrWhiteSpace(clothing)) sdPrompt += $", {clothing}";

                                        var sdArgObj = new { description = sdPrompt };
                                        var sdArgElement = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(sdArgObj));
                                        var relativeImagePath = await _plugins.ExecuteTool("generate_portrait", sdArgElement);

                                        if (!string.IsNullOrEmpty(relativeImagePath) && relativeImagePath.StartsWith("/uploads/"))
                                        {
                                            var imgMarkdown = $"\n\n![Selfie]({relativeImagePath})";
                                            await TrySend(new { type = "token", content = imgMarkdown }, CancellationToken.None);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.Error.WriteLine($"[chat] Failed to auto-generate selfie in background: {ex.Message}");
                                    }
                                });
                            }

                            var cleanResponseText = System.Text.RegularExpressions.Regex.Replace(
                                responseText, 
                                @"^\s*\*?<?\s*thought\s*>?.*?</?\s*thought\s*>\s*", 
                                "", 
                                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            ).Replace("<call>generate_portrait</call>", "").Trim();

                            if (isRegenerate && regenMessageId.HasValue)
                            {
                                existingSwipes.Add(cleanResponseText);
                                var jsonPayload = JsonSerializer.Serialize(new
                                {
                                    swipes = existingSwipes,
                                    active = existingSwipes.Count - 1
                                });
                                await _db.UpdateMessage(regenMessageId.Value, jsonPayload);
                                lock (history) { history.Add(new ChatMessage("assistant", cleanResponseText)); }
                            }
                            else
                            {
                                await _db.AddMessage(conversationId, "assistant", cleanResponseText, companionName);
                                lock (history) { history.Add(new ChatMessage("assistant", cleanResponseText)); }
                            }
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
            .Select(m => new ChatMessage(m.Role, ExtractActiveSwipe(m.Content)))
            .TakeLast(MaxHistoryMessages)
            .ToList();
        _convCache.Set(conversationId, history, CacheTtl);
    }

    public static string ExtractActiveSwipe(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        if (content.StartsWith("{\"swipes\":") && content.EndsWith("}"))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.TryGetProperty("swipes", out var swipesEl) && swipesEl.ValueKind == JsonValueKind.Array)
                {
                    var swipes = swipesEl.EnumerateArray().Select(s => s.GetString()).ToList();
                    int active = 0;
                    if (root.TryGetProperty("active", out var activeEl))
                    {
                        active = activeEl.GetInt32();
                    }
                    else
                    {
                        active = swipes.Count - 1;
                    }
                    if (active >= 0 && active < swipes.Count)
                    {
                        return swipes[active] ?? "";
                    }
                }
            }
            catch {}
        }
        return content;
    }

    private async Task HandleGroupChatMessage(
        WebSocket ws, 
        string groupId, 
        string userMessage, 
        string modelId, 
        string? requestedCompanionName, 
        int userId, 
        string username, 
        Func<object, CancellationToken, Task> safeSend, 
        Func<object, CancellationToken, Task> trySend, 
        CancellationToken ct)
    {
        try
        {
            // 1. Get the group room
            var groups = await _db.GetGroups(userId);
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group == null)
            {
                await trySend(new { type = "error", content = "Group room not found." }, ct);
                return;
            }

            // 2. If user message is present, save it to database
            if (!string.IsNullOrEmpty(userMessage))
            {
                await _db.SaveGroupMessage(groupId, "user", null, userMessage);
            }

            // 3. Determine which companion should speak
            var companionsInGroup = group.CharacterNames
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim())
                .ToList();

            if (companionsInGroup.Count == 0)
            {
                await trySend(new { type = "error", content = "No companions in this group chat room." }, ct);
                return;
            }

            var activeCompanionName = requestedCompanionName;
            if (string.IsNullOrEmpty(activeCompanionName))
            {
                // Fallback: check who spoke last, and pick the next companion in round-robin order
                var history = await _db.GetGroupMessages(groupId);
                var lastCompanionMsg = history.LastOrDefault(m => (m.Sender == "assistant" || m.Sender == "character") && !string.IsNullOrEmpty(m.CharacterName));
                
                if (lastCompanionMsg != null)
                {
                    var lastIndex = companionsInGroup.FindIndex(n => n.Equals(lastCompanionMsg.CharacterName, StringComparison.OrdinalIgnoreCase));
                    if (lastIndex >= 0)
                    {
                        activeCompanionName = companionsInGroup[(lastIndex + 1) % companionsInGroup.Count];
                    }
                }

                if (string.IsNullOrEmpty(activeCompanionName))
                {
                    activeCompanionName = companionsInGroup[0];
                }
            }

            // 4. Send speaker identity so UI knows who is typing
            await trySend(new { type = "group_speaker", character_name = activeCompanionName }, ct);
            await trySend(new { type = "typing", content = true }, ct);

            // 5. Build prompt
            var compClean = string.Concat(activeCompanionName.Split(Path.GetInvalidFileNameChars())).Trim();
            var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
            var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
            var localDir = Path.Combine(baseDir, "local");
            var localFile = Path.Combine(localDir, $"{compClean.ToLowerInvariant()}.json");
            var baseFile = Path.Combine(baseDir, $"{compClean.ToLowerInvariant()}.json");
            var checkFile = File.Exists(localFile) ? localFile : (File.Exists(baseFile) ? baseFile : null);

            var activeUser = await _db.GetUserById(userId);
            var systemPromptBuilder = new StringBuilder();
            if (activeUser != null)
            {
                systemPromptBuilder.AppendLine(AshServer.Personality.PersonalityLoader.BuildUserGenderDirective(activeUser.DisplayName ?? username, activeUser.Gender));
            }
            if (!string.IsNullOrEmpty(group.Scenario))
            {
                systemPromptBuilder.AppendLine($"[Scenario: {group.Scenario}]");
            }
            if (!string.IsNullOrEmpty(group.SystemPrompt))
            {
                systemPromptBuilder.AppendLine($"[Room System Prompt: {group.SystemPrompt}]");
            }
            systemPromptBuilder.AppendLine($"[You are playing as {activeCompanionName}. Keep in character. Reply using short messages, do not repeat yourself, and react to other characters naturally.]");

            if (checkFile != null)
            {
                var json = await System.IO.File.ReadAllTextAsync(checkFile);
                var comp = JsonSerializer.Deserialize<AshServer.Controllers.CompanionConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (comp != null && !string.IsNullOrEmpty(comp.SystemPrompt))
                {
                    systemPromptBuilder.AppendLine($"[{activeCompanionName}'s Personality System Prompt: {comp.SystemPrompt}]");
                }
            }

            // Retrieve companion's episodic relationship memories about the user
            try
            {
                var memories = await _db.GetCompanionMemories(userId, activeCompanionName, limit: 15);
                if (memories != null && memories.Count > 0)
                {
                    systemPromptBuilder.AppendLine($"[{activeCompanionName}'s Episodic Relationship Memories About User]:");
                    foreach (var mem in memories)
                    {
                        systemPromptBuilder.AppendLine($"- {mem.Fact}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[chat] Error loading companion memories: {ex.Message}");
            }

            // Automatic memory fact extraction from user message
            if (!string.IsNullOrWhiteSpace(userMessage))
            {
                try
                {
                    var lowerInput = userMessage.ToLowerInvariant();
                    if (lowerInput.Contains("my favorite") || lowerInput.Contains("i love ") || lowerInput.Contains("my name is") || lowerInput.Contains("i work as") || lowerInput.Contains("my birthday"))
                    {
                        await _db.SaveCompanionMemory(userId, activeCompanionName, "personal_fact", userMessage, 1);
                        Console.WriteLine($"[chat] Automatically saved episodic memory for '{activeCompanionName}': {userMessage}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[chat] Error saving automatic memory: {ex.Message}");
                }
            }

            var messages = new List<ChatMessage> { new("system", systemPromptBuilder.ToString()) };

            var dbMessages = await _db.GetGroupMessages(groupId);
            foreach (var msg in dbMessages.TakeLast(30))
            {
                if (msg.Sender == "user")
                {
                    messages.Add(new ChatMessage("user", $"User: {msg.Content}"));
                }
                else
                {
                    var senderName = msg.CharacterName ?? "Assistant";
                    if (senderName.Equals(activeCompanionName, StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Add(new ChatMessage("assistant", msg.Content));
                    }
                    else
                    {
                        messages.Add(new ChatMessage("user", $"{senderName}: {msg.Content}"));
                    }
                }
            }

            // 6. Generate reply
            var responseText = "";
            await foreach (var token in _backends.StreamChat(modelId, messages).WithCancellation(ct))
            {
                responseText += token;
                await trySend(new { type = "token", content = token }, ct);
            }

            await trySend(new { type = "typing", content = false }, ct);
            await trySend(new { type = "done" }, ct);

            // 7. Check for state overrides and selfie generation
            if (!string.IsNullOrEmpty(responseText))
            {
                var lowerResponse = responseText.ToLowerInvariant();
                var lowerUser = userMessage?.ToLowerInvariant() ?? "";

                // Parse state update tags in response (e.g. [LOCATION: beach], [OUTFIT: red dress], [MOOD: happy])
                try
                {
                    if (checkFile != null && System.IO.File.Exists(checkFile))
                    {
                        var json = await System.IO.File.ReadAllTextAsync(checkFile);
                        var compState = JsonSerializer.Deserialize<AshServer.Controllers.CompanionConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (compState != null)
                        {
                            bool stateChanged = false;
                            var locMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"\[LOCATION:\s*(.*?)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (locMatch.Success) { compState.CurrentLocation = locMatch.Groups[1].Value.Trim(); stateChanged = true; }
                            
                            var outfitMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"\[OUTFIT:\s*(.*?)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (outfitMatch.Success) { compState.CurrentOutfit = outfitMatch.Groups[1].Value.Trim(); stateChanged = true; }

                            var moodMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"\[MOOD:\s*(.*?)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (moodMatch.Success) { compState.CurrentMood = moodMatch.Groups[1].Value.Trim(); stateChanged = true; }

                            var clothMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"\[CLOTHING:\s*(.*?)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (clothMatch.Success) { compState.ClothingState = clothMatch.Groups[1].Value.Trim(); stateChanged = true; }

                            if (stateChanged)
                            {
                                var updatedJson = JsonSerializer.Serialize(compState, new JsonSerializerOptions { WriteIndented = true });
                                await System.IO.File.WriteAllTextAsync(checkFile, updatedJson);
                                Console.WriteLine($"[chat] Updated companion state for '{activeCompanionName}' on disk.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[chat] Error updating state overrides: {ex.Message}");
                }

                bool shouldGenSelfie = lowerResponse.Contains("<call>generate_portrait</call>") ||
                                       lowerResponse.Contains("<call name=\"generate_portrait\">") ||
                                       lowerResponse.Contains("<call>generate_portrait:") ||
                                       lowerUser.Contains("selfie") || lowerUser.Contains("picture") ||
                                       lowerUser.Contains("photo") || lowerUser.Contains("snapshot") ||
                                       lowerUser.Contains("send a pic") || lowerUser.Contains("show me");

                if (shouldGenSelfie)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            AshServer.Controllers.CompanionConfig? comp = null;
                            if (checkFile != null && System.IO.File.Exists(checkFile))
                            {
                                var json = await System.IO.File.ReadAllTextAsync(checkFile);
                                comp = JsonSerializer.Deserialize<AshServer.Controllers.CompanionConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            }

                            var details = comp?.Description ?? comp?.Personality ?? "";
                            var location = comp?.CurrentLocation ?? "";
                            var outfit = comp?.CurrentOutfit ?? "";
                            var mood = comp?.CurrentMood ?? "";
                            var clothing = comp?.ClothingState ?? "";

                            string? customPrompt = null;
                            var xmlMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"<call\s+name=[""']generate_portrait[""']>(.*?)</call>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                            if (xmlMatch.Success)
                            {
                                customPrompt = xmlMatch.Groups[1].Value.Trim();
                            }
                            if (string.IsNullOrEmpty(customPrompt))
                            {
                                var tagMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"<call>generate_portrait:\s*(.*?)</call>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                                if (tagMatch.Success)
                                {
                                    customPrompt = tagMatch.Groups[1].Value.Trim();
                                }
                            }

                            var sdPrompt = "";
                            var lightingVariations = new[] { "soft cinematic lighting", "golden hour lighting", "vibrant studio lights", "warm indoor ambiance", "dramatic lighting" };
                            var angleVariations = new[] { "close up portrait", "medium shot", "looking at camera, natural smile", "dynamic angle, eye contact" };
                            var randomTag = $"{lightingVariations[Random.Shared.Next(lightingVariations.Length)]}, {angleVariations[Random.Shared.Next(angleVariations.Length)]}, seed_{Random.Shared.Next(1000, 9999)}";

                            bool isNakedScene = lowerResponse.Contains("naked") || lowerResponse.Contains("undressed") || lowerResponse.Contains("nude") || lowerResponse.Contains("bare") ||
                                                lowerUser.Contains("naked") || lowerUser.Contains("undressed") || lowerUser.Contains("nude") || lowerUser.Contains("in bed");

                            if (!string.IsNullOrEmpty(customPrompt))
                            {
                                sdPrompt = $"{customPrompt}, {randomTag}";
                            }
                            else
                            {
                                sdPrompt = $"digital art portrait of {activeCompanionName}, highly detailed";
                                if (!string.IsNullOrWhiteSpace(details)) sdPrompt += $", {details}";
                                if (!string.IsNullOrWhiteSpace(location)) sdPrompt += $", at/in {location}";
                                
                                if (isNakedScene)
                                {
                                    sdPrompt += ", naked, undressed, intimate, in bed";
                                }
                                else
                                {
                                    if (!string.IsNullOrWhiteSpace(outfit)) sdPrompt += $", wearing {outfit}";
                                    if (!string.IsNullOrWhiteSpace(clothing)) sdPrompt += $", {clothing}";
                                }

                                if (!string.IsNullOrWhiteSpace(mood)) sdPrompt += $", {mood} expression";
                                sdPrompt += $", {randomTag}";
                            }

                            // Clean tag from final response text
                            responseText = System.Text.RegularExpressions.Regex.Replace(responseText, @"<call\s+name=[""']generate_portrait[""']>(.*?)</call>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                            responseText = System.Text.RegularExpressions.Regex.Replace(responseText, @"<call>generate_portrait:\s*(.*?)</call>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                            responseText = responseText.Replace("<call>generate_portrait</call>", "").Trim();

                            var sdArgObj = new { description = sdPrompt };
                            var sdArgElement = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(sdArgObj));
                            var relativeImagePath = await _plugins.ExecuteTool("generate_portrait", sdArgElement);

                            if (!string.IsNullOrEmpty(relativeImagePath) && relativeImagePath.StartsWith("/uploads/"))
                            {
                                var imgMarkdown = $"\n\n![Selfie]({relativeImagePath})";
                                await trySend(new { type = "token", content = imgMarkdown }, CancellationToken.None);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[chat] Failed to auto-generate selfie in background: {ex.Message}");
                        }
                    });
                }
            }

            await _db.SaveGroupMessage(groupId, "assistant", activeCompanionName, responseText);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[group-chat] Error processing group message");
            await trySend(new { type = "error", content = ex.Message }, ct);
        }
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

    private string GetCompanionSystemPrompt(string companionName, string? username, string? displayName, string? gender, string? convId = null)
    {
        var activeName = displayName ?? username;
        var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
        var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
        var localDir = Path.Combine(baseDir, "local");
        var compClean = string.Concat(companionName.Split(Path.GetInvalidFileNameChars())).Trim();
        var localFile = Path.Combine(localDir, $"{compClean.ToLowerInvariant()}.json");
        var baseFile = Path.Combine(baseDir, $"{compClean.ToLowerInvariant()}.json");
        var checkFile = System.IO.File.Exists(localFile) ? localFile : (System.IO.File.Exists(baseFile) ? baseFile : null);

        if (checkFile == null)
        {
            return _personality.GetSystemPrompt(username, displayName, gender);
        }

        try
        {
            var compContent = System.IO.File.ReadAllText(checkFile);
            var comp = JsonSerializer.Deserialize<AshServer.Controllers.CompanionConfig>(compContent, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase, 
                PropertyNameCaseInsensitive = true 
            });
            if (comp != null)
            {
                AshServer.Data.ConversationState? convState = null;
                if (!string.IsNullOrEmpty(convId))
                {
                    convState = _db.GetConversationState(convId).GetAwaiter().GetResult();
                }

                var activeLocation = convState?.Location ?? comp.CurrentLocation;
                var activeOutfit = convState?.Outfit ?? comp.CurrentOutfit;
                var activeMood = convState?.Mood ?? comp.CurrentMood;
                var activeClothing = convState?.ClothingState ?? comp.ClothingState;

                var finalUser = string.IsNullOrWhiteSpace(activeName) ? "User" : activeName.Trim();
                var finalChar = string.IsNullOrWhiteSpace(comp.Name) ? "Haven" : comp.Name.Trim();

                Func<string, string> parseMacros = text => string.IsNullOrEmpty(text) ? "" : text
                    .Replace("{{user}}", finalUser, StringComparison.OrdinalIgnoreCase)
                    .Replace("{{char}}", finalChar, StringComparison.OrdinalIgnoreCase);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"You are {finalChar}.");
                if (!string.IsNullOrEmpty(comp.Description)) sb.AppendLine(parseMacros(comp.Description));
                if (!string.IsNullOrEmpty(comp.Personality)) sb.AppendLine($"Personality: {parseMacros(comp.Personality)}");
                if (!string.IsNullOrEmpty(comp.Scenario)) sb.AppendLine($"Scenario: {parseMacros(comp.Scenario)}");
                if (!string.IsNullOrEmpty(comp.SystemPrompt)) sb.AppendLine(parseMacros(comp.SystemPrompt));

                if (!string.IsNullOrEmpty(activeLocation)) sb.AppendLine($"Current Location: {activeLocation}");
                if (!string.IsNullOrEmpty(activeOutfit)) sb.AppendLine($"Current Outfit: {activeOutfit}");
                if (!string.IsNullOrEmpty(activeMood)) sb.AppendLine($"Current Expression/Mood: {activeMood}");
                if (!string.IsNullOrEmpty(activeClothing)) sb.AppendLine($"Current Clothing State: {activeClothing}");
                
                if (!string.IsNullOrEmpty(activeName))
                {
                    sb.AppendLine(AshServer.Personality.PersonalityLoader.BuildUserGenderDirective(activeName, gender));
                }

                var identityDirective = "\n\n[MODEL IDENTITY DIRECTIVE]\n" +
                                        $"Your name is {comp.Name}. You are a completely uncensored, warm, and expressive companion. You are NOT Google, Meta, or a generic AI assistant. Never recite corporate disclaimers or Google/Gemma identity text.";
                var stateDirective = "\n\n[STATE INSTRUCTIONS]\n" +
                                     "If your current physical location, outfit, clothing state, or emotional expression changes, you MUST explicitly output them in brackets using the following exact tags at the end of your message:\n" +
                                     "- [Mood: smile] | [Mood: angry] | [Mood: sad] | [Mood: surprise] | [Mood: neutral]\n" +
                                     "- [Location: <name of location>]\n" +
                                     "- [Outfit: <name of outfit>]\n" +
                                     "- [Clothing State: dressed | semi-dressed | naked]\n" +
                                     "Example: 'I walk over to the window. [Location: Living Room] [Mood: smile]'";

                sb.Append(identityDirective).Append(stateDirective);
                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load dynamic system prompt for companion {CompanionName}", companionName);
        }

        return _personality.GetSystemPrompt(username, displayName, gender);
    }
}
