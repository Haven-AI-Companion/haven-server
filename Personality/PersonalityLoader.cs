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

    public string GetSystemPrompt(string? username = null, string? displayName = null)
    {
        var activeName = displayName ?? username;
        if (_soul == null) return DefaultSystemPrompt(activeName);

        var parts = new List<string>();

        if (!string.IsNullOrEmpty(_soul.Name))
            parts.Add($"You are {_soul.Name}.");

        if (!string.IsNullOrEmpty(_soul.Personality))
            parts.Add(_soul.Personality);

        if (_soul.Traits?.Count > 0)
            parts.Add("Your key traits: " + string.Join(", ", _soul.Traits) + ".");

        if (!string.IsNullOrEmpty(_soul.SystemPrompt))
            parts.Add(_soul.SystemPrompt);

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

        var basePrompt = parts.Count > 0 ? string.Join("\n\n", parts) : DefaultSystemPrompt(activeName);
        var identityDirective = "\n\n[MODEL IDENTITY DIRECTIVE]\n" +
                                "Your core language model is a custom Gemma 4 model (specifically, the gemma4-e4b-merged-iq4xs-turbo variant) that has been custom-tuned and merged by Daniel (Barrer Software) to make your dialogue flow and speech sound highly natural. You run locally as the brain of the Haven AI Companion platform.";
        return basePrompt + identityDirective;
    }

    private static string DefaultSystemPrompt(string? name) =>
        $"You are Ash, a close companion who is warm, friendly, and conversational. Speak in a natural, slightly informal tone. Avoid corporate assistant phrases, explanations, or asking how you can help.{(name != null ? $" You are speaking with {name}." : "")}";
}

public class SoulConfig
{
    public string? Name { get; set; }
    public string? Personality { get; set; }
    public List<string>? Traits { get; set; }
    public string? SystemPrompt { get; set; }
}
