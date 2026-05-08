using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using AshServer.Data;
using Microsoft.Extensions.Logging;

namespace AshServer.Chat.Discord;

/// <summary>
/// Discord bot integration as an ASP.NET Core hosted service.
/// Reads connection settings from ThirdPartyChat:Discord in appsettings.json.
/// Only starts when Enabled = true and a BotToken is present.
///
/// Per-message flow:
///   1. IdentityResolver.ResolveAsync → check if user/channel is allowed
///   2. Rate limit (per Discord user ID)
///   3. Route to DiscordMessageRouter → AI pipeline → response string
///   4. Post response back (split at Discord's 2000-char limit)
///
/// Self-service identity linking:
///   !link CODE  (or configured prefix + "link CODE")
///   → ConsumeLinkCode → AddIdentity → confirmation message
/// </summary>
public sealed class DiscordBot : IHostedService, IAsyncDisposable
{
    private readonly IConfiguration       _config;
    private readonly DiscordMessageRouter _router;
    private readonly IdentityResolver     _identity;
    private readonly Database             _db;
    private readonly ILogger<DiscordBot>  _logger;

    private DiscordSocketClient? _client;

    // (channelId:externalUserId) → DB conversationId
    private readonly ConcurrentDictionary<string, string> _conversations = new();

    // Simple per-user rate limit: externalUserId → time next message is allowed
    private readonly ConcurrentDictionary<string, DateTimeOffset> _rateLimits = new();
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(5);

    public DiscordBot(
        IConfiguration config,
        DiscordMessageRouter router,
        IdentityResolver identity,
        Database db,
        ILogger<DiscordBot> logger)
    {
        _config   = config;
        _router   = router;
        _identity = identity;
        _db       = db;
        _logger   = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_config.GetValue("ThirdPartyChat:Discord:Enabled", false))
        {
            _logger.LogInformation("[discord] Disabled — set ThirdPartyChat:Discord:Enabled=true to enable");
            return;
        }

        var token = _config["ThirdPartyChat:Discord:BotToken"]?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("[discord] Enabled but BotToken is empty — bot will not start");
            return;
        }

        var socketCfg = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
                           | GatewayIntents.GuildMessages
                           | GatewayIntents.DirectMessages
                           | GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Warning,
        };

        _client = new DiscordSocketClient(socketCfg);
        _client.Log             += OnLog;
        _client.Ready           += OnReady;
        _client.MessageReceived += OnMessageReceived;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        var statusText = _config["ThirdPartyChat:Discord:StatusText"]?.Trim();
        if (!string.IsNullOrEmpty(statusText))
            await _client.SetGameAsync(statusText);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_client is null) return;
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    // ── Discord events ────────────────────────────────────────────────────────

    private Task OnReady()
    {
        _logger.LogInformation("[discord] Logged in as {User} ({Id})",
            _client!.CurrentUser.Username, _client.CurrentUser.Id);
        return Task.CompletedTask;
    }

    private Task OnLog(LogMessage msg)
    {
        var level = msg.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error    => LogLevel.Error,
            LogSeverity.Warning  => LogLevel.Warning,
            LogSeverity.Info     => LogLevel.Information,
            _                    => LogLevel.Debug,
        };
        _logger.Log(level, msg.Exception, "[discord] {Message}", msg.Message);
        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(SocketMessage rawMsg)
    {
        // Only handle real user messages
        if (rawMsg is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;

        var prefix         = _config["ThirdPartyChat:Discord:CommandPrefix"] ?? "!";
        var content        = msg.Content.Trim();
        var channelId      = msg.Channel.Id.ToString();
        var externalId     = msg.Author.Id.ToString();
        var externalUser   = msg.Author.Username;
        bool isDm          = msg.Channel is IDMChannel;
        bool mentionsBot   = _client is not null
                             && msg.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);
        bool hasPrefix     = content.StartsWith(prefix, StringComparison.Ordinal);

        // ── Link command ────────────────────────────────────────────────────
        var linkCmd = prefix + "link";
        if (content.StartsWith(linkCmd, StringComparison.OrdinalIgnoreCase) &&
            (content.Length == linkCmd.Length || content[linkCmd.Length] == ' '))
        {
            await HandleLinkCommandAsync(msg, content, linkCmd);
            return;
        }

        // ── Reset command ───────────────────────────────────────────────────
        var resetCmd = prefix + "reset";
        if (content.Equals(resetCmd, StringComparison.OrdinalIgnoreCase))
        {
            var key = $"{channelId}:{externalId}";
            if (_conversations.TryGetValue(key, out var oldConv))
            {
                _router.InvalidateHistory(oldConv);
                _conversations.TryRemove(key, out _);
            }
            await msg.Channel.SendMessageAsync("🔄 Conversation history cleared.");
            return;
        }

        // ── Only respond when addressed ──────────────────────────────────────
        if (!isDm && !mentionsBot && !hasPrefix) return;

        // Strip prefix / mention to get the clean user message
        string userMessage;
        if (mentionsBot)
            userMessage = msg.CleanContent.Trim();
        else if (hasPrefix && !isDm)
            userMessage = content[prefix.Length..].Trim();
        else
            userMessage = content;

        if (string.IsNullOrWhiteSpace(userMessage)) return;

        // ── Rate limit ───────────────────────────────────────────────────────
        if (_rateLimits.TryGetValue(externalId, out var nextAllowed)
            && DateTimeOffset.UtcNow < nextAllowed)
        {
            await msg.Channel.SendMessageAsync(
                "⏱️ Please wait a moment before sending another message.");
            return;
        }
        _rateLimits[externalId] = DateTimeOffset.UtcNow.Add(RateWindow);

        // ── Identity / permission resolution ────────────────────────────────
        var resolved = await _identity.ResolveAsync("discord", channelId, externalId, externalUser);

        await _db.AddAuditEntry("discord", channelId, externalId, externalUser,
            resolved.UserId, "message",
            resolved.IsAllowed ? null : $"denied: {resolved.DenyReason}");

        if (!resolved.IsAllowed)
        {
            await msg.Channel.SendMessageAsync($"❌ {resolved.DenyReason}");
            return;
        }

        // ── Get or create conversation ───────────────────────────────────────
        var convKey = $"{channelId}:{externalId}";
        if (!_conversations.TryGetValue(convKey, out var conversationId)
            || string.IsNullOrEmpty(conversationId))
        {
            conversationId = await _db.CreateConversation(
                resolved.UserId ?? 0,
                $"Discord #{msg.Channel.Name ?? channelId}");
            _conversations[convKey] = conversationId;
        }

        // ── Route to AI ──────────────────────────────────────────────────────
        using var typing = msg.Channel.EnterTypingState();
        try
        {
            var response = await _router.RouteAsync(
                userId:         resolved.UserId ?? 0,
                username:       resolved.Username ?? externalUser,
                isAdmin:        false,
                permissions:    resolved.Permissions.ToHashSet(),
                message:        userMessage,
                conversationId: conversationId,
                agentEnabled:   resolved.AgentAllowed,
                maxTurns:       resolved.MaxTurns);

            await _db.AddAuditEntry("discord", channelId, externalId, externalUser,
                resolved.UserId, "response", $"{response.Length} chars");

            foreach (var chunk in SplitMessage(response))
                await msg.Channel.SendMessageAsync(chunk);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[discord] Error routing message from {User} in channel {Channel}",
                externalUser, channelId);
            await msg.Channel.SendMessageAsync(
                "⚠️ Something went wrong processing your message. Please try again.");
        }
    }

    // ── Link command handler ──────────────────────────────────────────────────

    private async Task HandleLinkCommandAsync(SocketUserMessage msg, string content, string linkCmd)
    {
        var externalId   = msg.Author.Id.ToString();
        var externalUser = msg.Author.Username;
        var parts        = content.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            var prefix = _config["ThirdPartyChat:Discord:CommandPrefix"] ?? "!";
            await msg.Channel.SendMessageAsync(
                "🔗 **Link your ash-server account**\n" +
                "1. Log in to the ash-server web interface\n" +
                "2. Go to your profile → **Linked Accounts** → Request a link code\n" +
                $"3. Run `{prefix}link YOUR_CODE` here to complete the link\n\n" +
                "_Codes expire after 10 minutes._");
            return;
        }

        var code   = parts[1].Trim().ToUpperInvariant();
        var result = await _db.ConsumeLinkCode(code);

        if (result is null)
        {
            await msg.Channel.SendMessageAsync(
                "❌ That code is invalid, expired, or already used.\n" +
                "Request a new one from the ash-server web interface.");
            return;
        }

        var (userId, provider) = result.Value;
        await _db.AddIdentity(userId, provider, externalId, externalUser);
        await _db.AddAuditEntry("discord", msg.Channel.Id.ToString(), externalId, externalUser,
            userId, "identity_linked");

        await msg.Channel.SendMessageAsync(
            "✅ **Account linked!** Your Discord account is now connected to your ash-server profile.\n" +
            "Your role permissions will apply to all future messages in configured channels.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Split a long response into chunks that fit Discord's 2000-char limit,
    /// trying to break on newlines where possible.
    /// </summary>
    private static IEnumerable<string> SplitMessage(string text, int maxLen = 1990)
    {
        if (text.Length <= maxLen) { yield return text; yield break; }

        var pos = 0;
        while (pos < text.Length)
        {
            var remaining = text.Length - pos;
            if (remaining <= maxLen) { yield return text[pos..]; yield break; }

            // Try to break on a newline within the allowed window
            var slice    = text.Substring(pos, maxLen);
            var breakAt  = slice.LastIndexOf('\n');
            if (breakAt < maxLen / 2) breakAt = -1; // don't break too early

            var chunkLen = breakAt > 0 ? breakAt + 1 : maxLen;
            yield return text.Substring(pos, chunkLen);
            pos += chunkLen;
            // Skip leading newlines on the next chunk
            while (pos < text.Length && text[pos] == '\n') pos++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
