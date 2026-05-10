using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AshServer.Chat.Discord;
using AshServer.Data;
using AshServer.Middleware;
using AshServer.Auth;
namespace AshServer.Chat.Slack;

/// <summary>
/// Handles Slack Events API payloads forwarded from SlackEventsController.
/// Registers as a singleton so the ASP.NET controller can inject and call HandleMessageAsync.
/// 
/// Slack setup:
///   1. Create a Slack App → Enable "Event Subscriptions"
///   2. Set Request URL: https://your-server/slack/events
///   3. Subscribe to bot events: message.channels, message.groups, message.im
///   4. Bot Token Scopes: chat:write, channels:history, groups:history, im:history
///   5. Copy Bot Token (xoxb-...) and Signing Secret into appsettings.json
/// </summary>
public sealed class SlackBot : IHostedService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SlackBot> _log;
    private readonly DiscordMessageRouter _router;
    private readonly Database _db;
    private readonly ExternalRateLimiter _rateLimiter;
    private readonly PromptGuard _guard;
    private readonly HttpClient _http;
    private readonly IdentityResolver _identityResolver;

    // (slackChannelId) → DB conversationId
    private readonly ConcurrentDictionary<string, string> _conversations = new();

    // Exposed for SlackEventsController
    public bool Enabled { get; private set; }
    public string SigningSecret { get; private set; } = "";
    private string _botToken = "";

    public SlackBot(IConfiguration config, ILogger<SlackBot> log,
                    DiscordMessageRouter router, Database db,
                    ExternalRateLimiter rateLimiter, PromptGuard guard,
                    IHttpClientFactory httpFactory, IdentityResolver identityResolver)
    {
        _config = config;
        _log = log;
        _router = router;
        _db = db;
        _rateLimiter = rateLimiter;
        _guard = guard;
        _http = httpFactory.CreateClient("slack");
        _identityResolver = identityResolver;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Enabled = _config.GetValue("ThirdPartyChat:Slack:Enabled", false);
        if (!Enabled)
        {
            _log.LogInformation("[slack] Disabled — set ThirdPartyChat:Slack:Enabled=true to enable");
            return Task.CompletedTask;
        }

        _botToken = _config["ThirdPartyChat:Slack:BotToken"] ?? "";
        SigningSecret = _config["ThirdPartyChat:Slack:SigningSecret"] ?? "";

        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(SigningSecret))
        {
            _log.LogWarning("[slack] BotToken and SigningSecret required. Configure ThirdPartyChat:Slack in appsettings.json");
            Enabled = false;
            return Task.CompletedTask;
        }

        _log.LogInformation("[slack] Ready — listening for events at POST /slack/events");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("[slack] Stopped");
        return Task.CompletedTask;
    }

    /// <summary>Verifies the X-Slack-Signature HMAC-SHA256 header with 5-minute replay protection.</summary>
    public bool VerifySignature(string timestamp, string rawBody, string signature)
    {
        if (string.IsNullOrWhiteSpace(SigningSecret)) return false;

        // Replay protection: reject requests older than 5 minutes
        if (!long.TryParse(timestamp, out var tsSeconds)) return false;
        var requestTime = DateTimeOffset.FromUnixTimeSeconds(tsSeconds);
        if (Math.Abs((DateTimeOffset.UtcNow - requestTime).TotalSeconds) > 300) return false;

        var baseStr = $"v0:{timestamp}:{rawBody}";
        var key = Encoding.UTF8.GetBytes(SigningSecret);
        var msg = Encoding.UTF8.GetBytes(baseStr);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(msg);
        var expected = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature));
    }

    /// <summary>Called by SlackEventsController for each incoming message event.</summary>
    public async Task HandleMessageAsync(string channelId, string userId, string text, string? threadTs)
    {
        var externalId = userId;
        var rateLimitKey = $"slack:{userId}";
        try
        {
            if (!_rateLimiter.TryAcquire(rateLimitKey))
            {
                var wait = (int)_rateLimiter.SecondsUntilReset(rateLimitKey);
                await PostMessageAsync(channelId, $"⏱ Slow down — try again in {wait}s.", threadTs);
                return;
            }

            var guard = _guard.Check(text, rateLimitKey);
            if (!guard.Safe)
            {
                _log.LogWarning("[slack] Prompt blocked from {Id}: {Reason}", rateLimitKey, guard.Reason);
                await PostMessageAsync(channelId, "⚠️ That message was blocked by safety filters.", threadTs);
                return;
            }

            // Resolve identity via IdentityResolver (provider="slack", channel=channelId, externalId=userId)
            var identity = await _identityResolver.ResolveAsync("slack", channelId, externalId, null);
            if (!identity.IsAllowed)
            {
                await PostMessageAsync(channelId, $"🔒 {identity.DenyReason}", threadTs);
                return;
            }

            var permissions = new HashSet<string>(identity.Permissions);
            if (!permissions.Contains(Permissions.ApiAccess))
            {
                await PostMessageAsync(channelId, "🔒 Your account does not have chat access.", threadTs);
                return;
            }

            var conversationId = await GetOrCreateConversationId(channelId, userId, identity.UserId);

            var response = await _router.RouteAsync(
                userId:         identity.UserId ?? 0,
                username:       identity.Username ?? $"slack:{userId}",
                isAdmin:        false,
                permissions:    permissions,
                message:        text,
                conversationId: conversationId,
                agentEnabled:   identity.AgentAllowed,
                maxTurns:       identity.MaxTurns);

            if (string.IsNullOrWhiteSpace(response)) response = "…";

            const int maxLen = 3900;
            for (int i = 0; i < response.Length; i += maxLen)
            {
                var chunk = response.Substring(i, Math.Min(maxLen, response.Length - i));
                await PostMessageAsync(channelId, chunk, threadTs);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[slack] Error handling message from {Id}", rateLimitKey);
        }
    }

    private async Task PostMessageAsync(string channelId, string text, string? threadTs)
    {
        var payload = new { channel = channelId, text, thread_ts = threadTs };
        var req = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            _log.LogWarning("[slack] chat.postMessage returned {Status}", resp.StatusCode);
    }

    private async Task<string> GetOrCreateConversationId(string channelId, string userId, int? linkedUserId)
    {
        var convKey = $"slack:{channelId}";
        if (_conversations.TryGetValue(convKey, out var existing)) return existing;

        var convs = await _db.GetConversationsForExternalChannel("slack", convKey);
        if (convs.Count > 0)
        {
            _conversations[convKey] = convs[0].Id;
            return convs[0].Id;
        }

        var convId = await _db.CreateConversation(linkedUserId ?? 0, $"Slack: {channelId}");
        await _db.CreateExternalConversation("slack", convKey, convId);
        _conversations[convKey] = convId;
        return convId;
    }
}

