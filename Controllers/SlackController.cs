using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using AshServer.Chat.Slack;

namespace AshServer.Controllers;

/// <summary>
/// Receives Slack Events API webhooks at POST /slack/events.
/// 
/// Setup in Slack App dashboard:
///   1. Enable "Event Subscriptions" → Request URL: https://your-server/slack/events
///   2. Subscribe to bot events: message.channels, message.groups, message.im
///   3. Enable Socket Mode OFF (we use Events API, not Socket Mode)
///   4. Bot Token Scopes: chat:write, channels:history, groups:history, im:history
/// </summary>
[ApiController]
[Route("slack")]
public sealed class SlackEventsController : ControllerBase
{
    private readonly SlackBot _bot;
    private readonly ILogger<SlackEventsController> _log;

    public SlackEventsController(SlackBot bot, ILogger<SlackEventsController> log)
    {
        _bot = bot;
        _log = log;
    }

    [HttpPost("events")]
    public async Task<IActionResult> Events()
    {
        if (!_bot.Enabled)
            return StatusCode(503, new { error = "Slack integration is disabled" });

        // Read raw body for signature verification
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        // Verify Slack signature
        var timestamp = Request.Headers["X-Slack-Request-Timestamp"].ToString();
        var signature = Request.Headers["X-Slack-Signature"].ToString();

        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
        {
            _log.LogWarning("[slack] Missing signature headers");
            return Unauthorized();
        }

        // Reject requests older than 5 minutes (replay attack protection)
        if (long.TryParse(timestamp, out var ts))
        {
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
            if (Math.Abs(age) > 300)
            {
                _log.LogWarning("[slack] Rejected stale request (age={Age}s)", age);
                return Unauthorized();
            }
        }

        if (!_bot.VerifySignature(timestamp, rawBody, signature))
        {
            _log.LogWarning("[slack] Invalid signature");
            return Unauthorized();
        }

        // Parse payload
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString();

        // URL verification handshake (one-time during app setup)
        if (type == "url_verification")
        {
            var challenge = root.GetProperty("challenge").GetString();
            return Ok(new { challenge });
        }

        // Event callback
        if (type == "event_callback" && root.TryGetProperty("event", out var evt))
        {
            var evtType = evt.GetProperty("type").GetString();

            if (evtType == "message")
            {
                // Ignore bot messages and subtypes (edits, joins, etc.)
                if (evt.TryGetProperty("bot_id", out _)) return Ok();
                if (evt.TryGetProperty("subtype", out _)) return Ok();

                var channelId = evt.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
                var userId = evt.TryGetProperty("user", out var u) ? u.GetString() : null;
                var text = evt.TryGetProperty("text", out var t) ? t.GetString() : null;
                var threadTs = evt.TryGetProperty("thread_ts", out var tts) ? tts.GetString() : null;

                if (!string.IsNullOrWhiteSpace(channelId) && !string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(text))
                {
                    // Respond immediately (Slack requires < 3s response), process async
                    _ = Task.Run(() => _bot.HandleMessageAsync(channelId, userId, text, threadTs));
                }
            }
        }

        return Ok();
    }
}
