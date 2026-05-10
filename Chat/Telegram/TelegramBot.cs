using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using AshServer.Auth;
using AshServer.Chat;
using AshServer.Chat.Discord;
using AshServer.Data;
using AshServer.Middleware;

namespace AshServer.Chat.Telegram;

public sealed class TelegramBot : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly DiscordMessageRouter _router;
    private readonly Database _db;
    private readonly ExternalRateLimiter _rateLimiter;
    private readonly PromptGuard _guard;
    private readonly ILogger<TelegramBot> _log;
    private readonly IdentityResolver _identityResolver;

    // (telegramChatId) → DB conversationId
    private readonly ConcurrentDictionary<string, string> _conversations = new();

    public TelegramBot(IConfiguration config, DiscordMessageRouter router, Database db,
        ExternalRateLimiter rateLimiter, PromptGuard guard, ILogger<TelegramBot> log,
        IdentityResolver identityResolver)
    {
        _config = config;
        _router = router;
        _db = db;
        _rateLimiter = rateLimiter;
        _guard = guard;
        _log = log;
        _identityResolver = identityResolver;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var botToken = _config["ThirdPartyChat:Telegram:BotToken"]?.Trim();
        var enabled = _config.GetValue("ThirdPartyChat:Telegram:Enabled", false);

        if (!enabled || string.IsNullOrWhiteSpace(botToken) || botToken.StartsWith("YOUR_"))
        {
            _log.LogInformation("[telegram] Disabled or not configured — skipping");
            return;
        }

        var bot = new TelegramBotClient(botToken);
        var me = await bot.GetMe(stoppingToken);
        _log.LogInformation("[telegram] Logged in as @{Username} ({Id})", me.Username, me.Id);

        var opts = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true
        };

        bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, opts, stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { } msg) return;
        if (msg.Text is not { } text) return;
        if (msg.From is null) return;

        var chatId = msg.Chat.Id.ToString();
        var userId = msg.From.Id.ToString();
        var username = msg.From.Username ?? msg.From.FirstName ?? "user";
        var rateLimitKey = $"telegram:{userId}";

        try
        {
            if (!_rateLimiter.TryAcquire(rateLimitKey))
            {
                var wait = (int)_rateLimiter.SecondsUntilReset(rateLimitKey);
                await bot.SendMessage(msg.Chat.Id, $"⏱ Slow down — try again in {wait}s.", cancellationToken: ct);
                return;
            }

            var guard = _guard.Check(text, rateLimitKey);
            if (!guard.Safe)
            {
                _log.LogWarning("[telegram] Prompt blocked from {Id}: {Reason}", rateLimitKey, guard.Reason);
                await bot.SendMessage(msg.Chat.Id, "⚠️ That message was blocked by safety filters.", cancellationToken: ct);
                return;
            }

            // Resolve identity via IdentityResolver
            var identity = await _identityResolver.ResolveAsync("telegram", chatId, userId, username, ct);
            if (!identity.IsAllowed)
            {
                await bot.SendMessage(msg.Chat.Id, $"🔒 {identity.DenyReason}", cancellationToken: ct);
                return;
            }

            var permissions = new HashSet<string>(identity.Permissions);
            if (!permissions.Contains(Permissions.ApiAccess))
            {
                await bot.SendMessage(msg.Chat.Id, "🔒 Your account does not have chat access.", cancellationToken: ct);
                return;
            }

            var convKey = $"telegram:{chatId}";
            var conversationId = await GetOrCreateConversationId(convKey, identity.Username ?? username, identity.UserId);

            await bot.SendChatAction(msg.Chat.Id, ChatAction.Typing, cancellationToken: ct);

            var response = await _router.RouteAsync(
                userId:         identity.UserId ?? 0,
                username:       identity.Username ?? username,
                isAdmin:        false,
                permissions:    permissions,
                message:        text,
                conversationId: conversationId,
                agentEnabled:   identity.AgentAllowed,
                maxTurns:       identity.MaxTurns,
                ct:             ct);

            if (string.IsNullOrWhiteSpace(response)) response = "…";

            foreach (var part in SplitMessage(response, 4096))
                await bot.SendMessage(msg.Chat.Id, part, cancellationToken: ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "[telegram] Error handling message from {Id}", rateLimitKey);
            try { await bot.SendMessage(msg.Chat.Id, "⚠️ Something went wrong. Please try again.", cancellationToken: ct); } catch { }
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, HandleErrorSource src, CancellationToken ct)
    {
        _log.LogError(ex, "[telegram] Receiver error ({Source})", src);
        return Task.CompletedTask;
    }

    private async Task<string> GetOrCreateConversationId(string convKey, string username, int? linkedUserId)
    {
        if (_conversations.TryGetValue(convKey, out var existing)) return existing;

        var convs = await _db.GetConversationsForExternalChannel("telegram", convKey);
        if (convs.Count > 0)
        {
            _conversations[convKey] = convs[0].Id;
            return convs[0].Id;
        }

        var convId = await _db.CreateConversation(linkedUserId ?? 0, $"Telegram: {username}");
        await _db.CreateExternalConversation("telegram", convKey, convId);
        _conversations[convKey] = convId;
        return convId;
    }

    private static IEnumerable<string> SplitMessage(string text, int maxLen)
    {
        for (int i = 0; i < text.Length; i += maxLen)
            yield return text.Substring(i, Math.Min(maxLen, text.Length - i));
    }
}
