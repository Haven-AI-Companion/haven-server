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
            var genderStr = !string.IsNullOrEmpty(gender) ? $", who is {gender}" : "";
            parts.Add($"You are speaking with {activeName}{genderStr}. Always address them by this name and align your references to their gender.");
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
                                "Your core language model is a custom Gemma 4 model (specifically, the gemma4-e4b-merged-iq4xs-turbo variant) that has been custom-tuned and merged by Daniel (Barrer Software) to make your dialogue flow and speech sound highly natural. You run locally as the brain of the Haven AI Companion platform.";
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
        var genderStr = !string.IsNullOrEmpty(gender) ? $", who is {gender}" : "";
        return $"You are Ash, a close companion who is warm, friendly, and conversational. Speak in a natural, slightly informal tone. Avoid corporate assistant phrases, explanations, or asking how you can help.{(name != null ? $" You are speaking with {name}{genderStr}." : "")}";
    }
}

public class SoulConfig
{
    public string? Name { get; set; }
    public string? Personality { get; set; }
    public List<string>? Traits { get; set; }
    public string? SystemPrompt { get; set; }
}
