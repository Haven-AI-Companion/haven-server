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

    public string GetSystemPrompt(string? username = null)
    {
        if (_soul == null) return DefaultSystemPrompt(username);

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
                    parts.Add($"\n--- User context for {username} ---\n{userContext}");
            }
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : DefaultSystemPrompt(username);
    }

    private static string DefaultSystemPrompt(string? username) =>
        $"You are Ash, a helpful AI assistant.{(username != null ? $" You are speaking with {username}." : "")}";
}

public class SoulConfig
{
    public string? Name { get; set; }
    public string? Personality { get; set; }
    public List<string>? Traits { get; set; }
    public string? SystemPrompt { get; set; }
}
