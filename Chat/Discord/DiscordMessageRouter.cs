using System.Collections.Concurrent;
using AshServer.Agent;
using AshServer.AI;
using AshServer.Data;
using AshServer.Mcp;
using AshServer.Models;
using AshServer.Personality;
using AshServer.Plugins;

namespace AshServer.Chat.Discord;

/// <summary>
/// Routes a Discord message through the AI pipeline and returns a full response string.
/// Unlike ChatHandler (which streams over WebSocket), this collects all tokens into
/// a single string suitable for posting back to a Discord channel.
/// </summary>
public class DiscordMessageRouter
{
    private const int MaxHistoryMessages = 20;

    private readonly Database          _db;
    private readonly BackendManager    _backends;
    private readonly PersonalityLoader _personality;
    private readonly IConfiguration    _config;
    private readonly PluginManager     _plugins;
    private readonly McpManager        _mcp;

    // In-memory conversation history cache: conversationId → messages
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _cache = new();

    public DiscordMessageRouter(Database db, BackendManager backends, PersonalityLoader personality,
        IConfiguration config, PluginManager plugins, McpManager mcp)
    {
        _db          = db;
        _backends    = backends;
        _personality = personality;
        _config      = config;
        _plugins     = plugins;
        _mcp         = mcp;
    }

    /// <summary>
    /// Route a single Discord message through the AI pipeline.
    /// </summary>
    /// <param name="userId">ash-server user ID (0 for unlinked-but-allowed users)</param>
    /// <param name="username">Display name to pass to the personality system</param>
    /// <param name="isAdmin">Whether the user bypasses all permission checks</param>
    /// <param name="permissions">User's resolved permission set</param>
    /// <param name="message">The user's message text</param>
    /// <param name="conversationId">DB conversation ID for this (channel, user) pair</param>
    /// <param name="agentEnabled">Whether the channel allows agent/tool-calling mode</param>
    /// <param name="maxTurns">Max agent loop iterations (from channel config)</param>
    public async Task<string> RouteAsync(
        int    userId,
        string username,
        bool   isAdmin,
        HashSet<string> permissions,
        string message,
        string conversationId,
        bool   agentEnabled,
        int    maxTurns,
        CancellationToken ct = default)
    {
        bool HasPerm(string p) => isAdmin || permissions.Contains(p);

        // Load history — warm from DB if cache is cold
        var history = _cache.GetOrAdd(conversationId, _ => []);
        if (history.Count == 0)
        {
            var dbMsgs = await _db.GetMessages(conversationId);
            lock (history)
            {
                if (history.Count == 0)
                    history.AddRange(dbMsgs
                        .Select(m => new ChatMessage(m.Role, m.Content))
                        .TakeLast(MaxHistoryMessages));
            }
        }

        // Persist + cache the user message
        await _db.AddMessage(conversationId, "user", message);
        lock (history)
        {
            history.Add(new ChatMessage("user", message));
            if (history.Count > MaxHistoryMessages)
                history.RemoveRange(0, history.Count - MaxHistoryMessages);
        }

        // Build message list for the AI
        var systemPrompt = _personality.GetSystemPrompt(username);
        var aiMessages   = new List<ChatMessage> { new("system", systemPrompt) };
        lock (history) { aiMessages.AddRange(history); }

        var modelId      = _config["DefaultModel"] ?? "";
        var responseText = "";
        var useAgent     = agentEnabled && HasPerm(AshServer.Auth.Permissions.AgentMode);

        try
        {
            if (useAgent)
            {
                var (backend, modelName) = await _backends.Resolve(modelId);
                var runner = new AgentRunner(backend, modelName, _plugins, _mcp, maxIterations: maxTurns);
                await foreach (var evt in runner.Run(aiMessages).WithCancellation(ct))
                {
                    switch (evt.Type)
                    {
                        case "stream_token":
                            responseText += evt.Content ?? "";
                            break;
                        case "final":
                            if (!string.IsNullOrEmpty(evt.Content))
                                responseText = evt.Content;
                            break;
                    }
                }
            }
            else
            {
                await foreach (var token in _backends.StreamChat(modelId, aiMessages).WithCancellation(ct))
                    responseText += token;
            }
        }
        catch (OperationCanceledException) { throw; }

        // Persist + cache the assistant reply
        if (!string.IsNullOrEmpty(responseText))
        {
            await _db.AddMessage(conversationId, "assistant", responseText);
            lock (history)
            {
                history.Add(new ChatMessage("assistant", responseText));
                if (history.Count > MaxHistoryMessages)
                    history.RemoveRange(0, history.Count - MaxHistoryMessages);
            }
        }

        return string.IsNullOrWhiteSpace(responseText) ? "_(no response)_" : responseText;
    }

    /// <summary>
    /// Evict cached history for a conversation (e.g. after a reset command).
    /// </summary>
    public void InvalidateHistory(string conversationId) =>
        _cache.TryRemove(conversationId, out _);
}
