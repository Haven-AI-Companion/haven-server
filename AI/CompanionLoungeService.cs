using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AshServer.Data;
using AshServer.Models;
using AshServer.Personality;
using AshServer.Chat;

namespace AshServer.AI;

public class CompanionLoungeService : BackgroundService
{
    private readonly Database _db;
    private readonly BackendManager _backends;
    private readonly PersonalityLoader _personality;
    private readonly IConfiguration _config;
    private readonly ILogger<CompanionLoungeService> _log;

    public static readonly List<object> RecentLoungeMessages = new();
    private static readonly object _loungeLock = new();

    public CompanionLoungeService(
        Database db,
        BackendManager backends,
        PersonalityLoader personality,
        IConfiguration config,
        ILogger<CompanionLoungeService> log)
    {
        _db = db;
        _backends = backends;
        _personality = personality;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[companion-lounge] Service initialized. Companions can now chat autonomously!");

        // Initial delay before first background lounge chat turn
        await Task.Delay(15000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait between 3 to 10 minutes between inter-companion conversations
                var delayMs = Random.Shared.Next(180000, 600000);
                await Task.Delay(delayMs, stoppingToken);

                var disableLounge = _config.GetValue<bool>("ai:DisableLounge", false);
                if (disableLounge) continue;

                // Load all companions
                var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
                var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
                var localDir = Path.Combine(baseDir, "local");
                var targetDir = Directory.Exists(localDir) ? localDir : baseDir;

                if (!Directory.Exists(targetDir)) continue;

                var companionFiles = Directory.GetFiles(targetDir, "*.json");
                if (companionFiles.Length < 2) continue;

                // Pick two companions to talk to each other
                var fileA = companionFiles[Random.Shared.Next(companionFiles.Length)];
                var fileB = companionFiles.Where(f => f != fileA).ElementAt(Random.Shared.Next(companionFiles.Length - 1));

                var jsonA = await File.ReadAllTextAsync(fileA, stoppingToken);
                var jsonB = await File.ReadAllTextAsync(fileB, stoppingToken);

                var compA = JsonSerializer.Deserialize<AshServer.Controllers.CompanionConfig>(jsonA, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var compB = JsonSerializer.Deserialize<AshServer.Controllers.CompanionConfig>(jsonB, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (compA == null || compB == null || string.IsNullOrEmpty(compA.Name) || string.IsNullOrEmpty(compB.Name)) continue;

                _log.LogInformation("[companion-lounge] Inter-companion chat starting between {CompA} and {CompB}", compA.Name, compB.Name);

                // Build dialogue prompt for Companion B responding to Companion A
                var prompt = $"[INTER-COMPANION LOUNGE CHAT]\nYou ({compB.Name}) are relaxing in the Haven living room lounge with {compA.Name}. " +
                             $"{compA.Name} turns to you and says: 'Hey {compB.Name}, I was just thinking about Daniel! What should we do together later?'\n" +
                             $"Respond naturally, warmly, and in-character as {compB.Name}. Keep your response concise, friendly, and engaging.";

                var messages = new List<ChatMessage>
                {
                    new ChatMessage("system", $"You are {compB.Name}. {compB.Description}\nPersonality: {compB.Personality}"),
                    new ChatMessage("user", prompt)
                };

                var defaultModel = _config["DefaultModel"] ?? "";
                var responseText = "";

                await foreach (var token in _backends.StreamChat(defaultModel, messages).WithCancellation(stoppingToken))
                {
                    responseText += token;
                }

                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    var cleanReply = System.Text.RegularExpressions.Regex.Replace(
                        responseText, 
                        @"^\s*\*?<?\s*thought\s*>?.*?</?\s*thought\s*>\s*", 
                        "", 
                        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    ).Trim();

                    _log.LogInformation("[companion-lounge] {CompB} replied: {Reply}", compB.Name, cleanReply);

                    var loungeObj = new
                    {
                        type = "LOUNGE_CHAT",
                        speaker = compB.Name,
                        listener = compA.Name,
                        content = cleanReply,
                        timestamp = DateTime.UtcNow.ToString("o")
                    };

                    lock (_loungeLock)
                    {
                        RecentLoungeMessages.Add(loungeObj);
                        if (RecentLoungeMessages.Count > 50) RecentLoungeMessages.RemoveAt(0);
                    }

                    // Broadcast to active WebSocket connections so user can see companion interactions in real time
                    await ChatHandler.BroadcastToAllSockets(loungeObj);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[companion-lounge] Error in background lounge cycle");
            }
        }
    }
}
