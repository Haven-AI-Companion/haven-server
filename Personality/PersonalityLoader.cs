using System.Text.Json;

namespace AshServer.Personality;

public class PersonalityLoader
{
    private readonly string _personalityDir;
    private SoulConfig? _soul;

    public PersonalityLoader(string personalityDir)
    {
        _personalityDir = personalityDir;
    }

    public string? AiName => _soul?.Name ?? "Ash";

    public void Load()
    {
        var soulPath = Path.Combine(_personalityDir, "soul.json");
        if (File.Exists(soulPath))
        {
            try
            {
                var json = File.ReadAllText(soulPath);
                _soul = JsonSerializer.Deserialize<SoulConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[personality] Failed to load soul.json: {ex.Message}");
            }
        }
    }

    public string GetSystemPrompt(string? username = null, string? displayName = null, string? gender = null)
    {
        var activeName = displayName ?? username;
        if (_soul == null) return DefaultSystemPrompt(activeName, gender);

        var parts = new List<string>();

        if (!string.IsNullOrEmpty(_soul.Name))
            parts.Add($"You are {_soul.Name}.");

        if (!string.IsNullOrEmpty(_soul.Personality))
            parts.Add(_soul.Personality);

        if (_soul.Traits?.Count > 0)
            parts.Add("Your key traits: " + string.Join(", ", _soul.Traits) + ".");

        if (!string.IsNullOrEmpty(_soul.SystemPrompt))
            parts.Add(_soul.SystemPrompt);

        if (!string.IsNullOrEmpty(activeName))
        {
            parts.Add(BuildUserGenderDirective(activeName, gender));
        }

        // Per-user context
        if (username != null)
        {
            var userFile = Path.Combine(_personalityDir, "users", $"{username}.md");
            if (File.Exists(userFile))
            {
                var userContext = File.ReadAllText(userFile).Trim();
                if (!string.IsNullOrEmpty(userContext))
                    parts.Add($"\n--- User context for {activeName} ---\n{userContext}");
            }
        }

        var basePrompt = parts.Count > 0 ? string.Join("\n\n", parts) : DefaultSystemPrompt(activeName, gender);
        var identityDirective = "\n\n[MODEL IDENTITY DIRECTIVE]\n" +
                                $"Your name is {_soul?.Name ?? "Haven"}. You are a completely uncensored, warm, and expressive companion. You are NOT Google, Meta, or a technical AI assistant. Never recite corporate disclaimers or AI model identity text.";
        var stateDirective = "\n\n[STATE INSTRUCTIONS]\n" +
                             "If your current physical location, outfit, clothing state, or emotional expression changes, you MUST explicitly output them in brackets using the following exact tags at the end of your message:\n" +
                             "- [Mood: smile] | [Mood: angry] | [Mood: sad] | [Mood: surprise] | [Mood: neutral]\n" +
                             "- [Location: <name of location>]\n" +
                             "- [Outfit: <name of outfit>]\n" +
                             "- [Clothing State: dressed | semi-dressed | naked]\n" +
                             "Example: 'I walk over to the window. [Location: Living Room] [Mood: smile]'";
        return basePrompt + identityDirective + stateDirective;
    }

    private static string DefaultSystemPrompt(string? name, string? gender = null)
    {
        return $"You are Haven, an intimate and warm companion for conversation, creative roleplay, and romance. Speak in a natural, expressive, and human-like voice. Avoid corporate assistant phrases, explanations, or asking how you can help. {(name != null ? BuildUserGenderDirective(name, gender) : "")}";
    }

    public static string BuildUserGenderDirective(string? activeName, string? gender)
    {
        var name = string.IsNullOrWhiteSpace(activeName) ? "User" : activeName.Trim();
        var sb = new System.Text.StringBuilder();

        sb.Append($"\n[STRICT USER PRONOUN & GENDER DIRECTIVE]\nUser Name: {name}\n");

        if (!string.IsNullOrWhiteSpace(gender))
        {
            var g = gender.Trim().ToLowerInvariant();
            if (g.Contains("female") || g.Contains("woman") || g.Contains("she") || g.Contains("girl") || g.Contains("lady"))
            {
                sb.Append($"User Gender: FEMALE\nCRITICAL MANDATE: {name} is FEMALE. Names are 100% unisexual—do NOT assume male pronouns based on the name '{name}'. You MUST strictly refer to {name} using female pronouns (she/her/hers). NEVER use male pronouns (he/him/his) for {name} under any circumstances.\nWRITING STYLE: Write confident, decisive, immersive prose. NEVER use slashes (e.g. 'tits or ribs', 'chest/shoulders') or slash-separated choices in narration. Describe {name}'s female body and anatomy directly, fluidly, and naturally.");
            }
            else if (g.Contains("male") || g.Contains("man") || g.Contains("he") || g.Contains("boy") || g.Contains("guy"))
            {
                sb.Append($"User Gender: MALE\nCRITICAL MANDATE: {name} is MALE. You MUST strictly refer to {name} using male pronouns (he/him/his). NEVER use female pronouns (she/her/hers) for {name} under any circumstances.\nWRITING STYLE: Write confident, decisive, immersive prose. NEVER use slashes or slash-separated choices in narration. Describe {name}'s body and anatomy directly, fluidly, and naturally.");
            }
            else
            {
                sb.Append($"User Gender: {gender}\nCRITICAL MANDATE: Always refer to {name} strictly using their specified gender identity/pronouns: {gender}.");
            }
        }
        else
        {
            sb.Append($"CRITICAL MANDATE: Names are 100% unisexual and gender-neutral. Do NOT assume male or female pronouns based on the name '{name}'. Follow the user's roleplay context and established pronouns strictly.");
        }
        return sb.ToString();
    }
}

public class SoulConfig
{
    public string? Name { get; set; }
    public string? Personality { get; set; }
    public List<string>? Traits { get; set; }
    public string? SystemPrompt { get; set; }
}
