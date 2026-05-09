using System.Collections.Concurrent;

namespace AshServer.Middleware;

/// <summary>
/// Sliding-window rate limiter for external chat users (Discord, Slack, Telegram).
/// Thread-safe; keyed by arbitrary string (e.g. "discord:{userId}" or "discord:{channelId}:{userId}").
///
/// Usage:
///   if (!_rateLimiter.TryAcquire("discord:" + userId))
///       // deny
/// </summary>
public sealed class ExternalRateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;

    // key → queue of timestamps within the current window
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows = new();
    private readonly ILogger<ExternalRateLimiter> _logger;

    public ExternalRateLimiter(IConfiguration config, ILogger<ExternalRateLimiter> logger)
    {
        _logger = logger;
        _maxRequests = config.GetValue("RateLimit:External:MaxRequests", 5);
        _window = TimeSpan.FromSeconds(config.GetValue("RateLimit:External:WindowSeconds", 10));
    }

    /// <summary>
    /// Returns true if the request is allowed, false if rate limit is exceeded.
    /// </summary>
    public bool TryAcquire(string key, int? maxRequests = null, TimeSpan? window = null)
    {
        var max = maxRequests ?? _maxRequests;
        var win = window ?? _window;
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - win;

        var queue = _windows.GetOrAdd(key, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            // Evict timestamps outside the window
            while (queue.Count > 0 && queue.Peek() < cutoff)
                queue.Dequeue();

            if (queue.Count >= max)
            {
                _logger.LogWarning("[rate-limit] {Key} exceeded {Max} req/{Window}s",
                    key, max, win.TotalSeconds);
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Returns how many seconds until the oldest request in the window expires.
    /// Returns 0 if not currently rate-limited.
    /// </summary>
    public double SecondsUntilReset(string key, TimeSpan? window = null)
    {
        var win = window ?? _window;
        if (!_windows.TryGetValue(key, out var queue)) return 0;
        lock (queue)
        {
            if (queue.Count == 0) return 0;
            var retry = (queue.Peek() + win - DateTimeOffset.UtcNow).TotalSeconds;
            return retry > 0 ? Math.Ceiling(retry) : 0;
        }
    }
}
