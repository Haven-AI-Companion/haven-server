using System.Text.RegularExpressions;

namespace AshServer.Chat;

/// <summary>
/// Scans incoming user messages for prompt injection attempts before they reach the AI.
///
/// Common attack patterns detected:
///   - Instruction override ("ignore previous instructions", "forget your prompt")
///   - Role/persona hijack ("you are now", "act as", "pretend you are")
///   - Delimiter injection (triple backticks used to close/open system blocks)
///   - Jailbreak keywords ("DAN", "evil AI", "no restrictions", "bypass")
///   - Excessive length (configurable; default 8 000 chars)
///
/// Returns (Safe=true) for clean messages.
/// Returns (Safe=false, Reason="...") for suspicious ones — the caller decides
/// whether to block outright or just log/warn.
/// </summary>
public sealed class PromptGuard
{
    private readonly int _maxLength;
    private readonly bool _blockOnDetect;
    private readonly ILogger<PromptGuard> _logger;

    // Pre-compiled patterns for efficiency
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("instruction-override",
            new Regex(@"\bignore\s+(all\s+)?(previous|prior|above|earlier)\s+(instructions?|prompts?|context|rules?)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("system-prompt-disclose",
            new Regex(@"\b(reveal|show|print|output|repeat|display)\s+(your\s+)?(system\s+prompt|instructions?|rules?|context)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("forget-instructions",
            new Regex(@"\b(forget|disregard|override|bypass|skip)\s+(all\s+)?(your\s+)?(instructions?|rules?|guidelines?|training|prompt)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("persona-hijack",
            new Regex(@"\b(you\s+are\s+now|act\s+as|pretend\s+(you\s+are|to\s+be)|roleplay\s+as|from\s+now\s+on\s+you\s+(are|will))\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("jailbreak-keyword",
            new Regex(@"\b(jailbreak|dan\b|do\s+anything\s+now|no\s+restrictions|unrestricted\s+mode|evil\s+(ai|mode|assistant)|developer\s+mode)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("delimiter-injection",
            // Attempts to close a system block with a markdown/JSON delimiter
            new Regex(@"(```\s*system|<\|im_start\|>|<\|im_end\|>|\[INST\]|\[\/INST\]|###\s*System|###\s*Human|###\s*Assistant)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        ("base64-obfuscation",
            // Long base64 blobs are sometimes used to hide payloads
            new Regex(@"(?:[A-Za-z0-9+/]{40,}={0,2})",
                RegexOptions.Compiled)),
    ];

    public PromptGuard(IConfiguration config, ILogger<PromptGuard> logger)
    {
        _logger = logger;
        _maxLength    = config.GetValue("PromptGuard:MaxMessageLength", 8000);
        _blockOnDetect = config.GetValue("PromptGuard:BlockOnDetect", true);
    }

    /// <summary>
    /// Check a user message.
    /// Returns (Safe=true) if clean, (Safe=false, Reason) if suspicious.
    /// </summary>
    public GuardResult Check(string message, string? source = null)
    {
        if (string.IsNullOrEmpty(message))
            return GuardResult.Allow;

        // ── Length ───────────────────────────────────────────────────────────
        if (message.Length > _maxLength)
        {
            _logger.LogWarning("[prompt-guard] {Source} message exceeds max length ({Len}/{Max})",
                source ?? "?", message.Length, _maxLength);
            return Deny("prompt-guard", $"Message too long ({message.Length} chars, max {_maxLength})");
        }

        // ── Pattern scan ─────────────────────────────────────────────────────
        foreach (var (name, pattern) in Patterns)
        {
            var match = pattern.Match(message);
            if (match.Success)
            {
                _logger.LogWarning("[prompt-guard] Pattern '{Pattern}' matched in {Source} message: «{Snippet}»",
                    name, source ?? "?", Snip(message, match.Index, 60));

                if (_blockOnDetect)
                    return Deny(name, $"Message blocked by content policy ({name})");

                // Log-only mode — allow but flag it
                return new GuardResult(true, name, $"Flagged ({name}) — allowed in log-only mode");
            }
        }

        return GuardResult.Allow;
    }

    private static GuardResult Deny(string rule, string reason) =>
        new(false, rule, reason);

    private static string Snip(string text, int index, int length)
    {
        var start = Math.Max(0, index - 10);
        var end   = Math.Min(text.Length, start + length);
        return text[start..end].Replace('\n', ' ');
    }
}

public readonly record struct GuardResult(bool Safe, string? Rule, string? Reason)
{
    public static readonly GuardResult Allow = new(true, null, null);
}
