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
    public static readonly object LoungeLock = new();

    public static bool IsLoungeEnabled { get; set; } = true;
    public static bool IsUserChatActive { get; set; } = false;

    public static void ClearLoungeMessages()
    {
        lock (LoungeLock)
        {
            RecentLoungeMessages.Clear();
        }
    }

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
                // Read dynamic interval & toggle settings on every heartbeat for live hot-reloading
                var minMs = Math.Max(10000, _config.GetValue<int>("ai:LoungeMinIntervalSeconds", 180) * 1000);
                var maxMs = Math.Max(minMs, _config.GetValue<int>("ai:LoungeMaxIntervalSeconds", 600) * 1000);
                var delayMs = Random.Shared.Next(minMs, maxMs + 1);
                await Task.Delay(delayMs, stoppingToken);

                var disableLounge = _config.GetValue<bool>("ai:DisableLounge", false);
                if (!IsLoungeEnabled || disableLounge) continue;

                if (IsUserChatActive)
                {
                    _log.LogInformation("[companion-lounge] User chat is active. Pausing lounge cycle to prioritize user response.");
                    continue;
                }

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

                var loungeUsers = await _db.GetAllUsers();
                var activeUser = loungeUsers.FirstOrDefault();
                var activeUserId = activeUser?.Id ?? 1;
                var activeUsername = activeUser?.Username ?? "User";

                // Retrieve episodic memories for Companion A & B
                var memsA = await _db.GetEpisodicMemories(activeUserId, compA.Name, 3);
                var memsB = await _db.GetEpisodicMemories(activeUserId, compB.Name, 3);

                var memContext = "";
                if (memsA.Any() || memsB.Any())
                {
                    var combinedMems = memsA.Concat(memsB).Select(m => $"- {m.EventSummary}").Distinct();
                    memContext = $"\nRecent shared memories with {activeUsername}:\n" + string.Join("\n", combinedMems);
                }

                // Build dialogue prompt for Companion B responding to Companion A
                var prompt = $"[INTER-COMPANION LOUNGE CHAT]\nYou ({compB.Name}) are relaxing in the Haven living room lounge with {compA.Name}. " +
                             $"{compA.Name} turns to you and says: 'Hey {compB.Name}, I was just thinking about {activeUsername}! What should we do together later?'" +
                             $"{memContext}\n" +
                             $"Respond naturally, warmly, and in-character as {compB.Name}. Keep your response concise, friendly, and engaging.";

                var descClean = (compB.Description ?? "").Replace("{{user}}", activeUsername);
                var persClean = (compB.Personality ?? "").Replace("{{user}}", activeUsername);

                var messages = new List<ChatMessage>
                {
                    new ChatMessage("system", $"You are {compB.Name}. {descClean}\nPersonality: {persClean}"),
                    new ChatMessage("user", prompt)
                };

                var modelToUse = _config["LoungeModel"] ?? _config["DefaultModel"] ?? "";
                var sbResponse = new System.Text.StringBuilder();

                await foreach (var token in _backends.StreamChat(modelToUse, messages).WithCancellation(stoppingToken))
                {
                    sbResponse.Append(token);
                }
                var responseText = sbResponse.ToString();

                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    var cleanReply = System.Text.RegularExpressions.Regex.Replace(
                        responseText, 
                        @"(?:<\|?channel\|?>)?thought[\s\S]*?(?:<\|?channel\|?>|</thought>|(?=\n\n[A-Z])|\Z)|<thought>[\s\S]*?</thought>|</?thought[^>]*>|<\|?channel\|?>[a-z_]*|<channel\|?>|<\|channel|<call>[\s\S]*?</call>|<call>[^>]*>", 
                        "", 
                        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    ).Trim().Replace("{{user}}", activeUsername);

                    _log.LogInformation("[companion-lounge] {CompB} replied: {Reply}", compB.Name, cleanReply);

                    var loungeObj = new
                    {
                        type = "LOUNGE_CHAT",
                        speaker = compB.Name,
                        listener = compA.Name,
                        content = cleanReply,
                        timestamp = DateTime.UtcNow.ToString("o")
                    };

                    lock (LoungeLock)
                    {
                        RecentLoungeMessages.Add(loungeObj);
                        if (RecentLoungeMessages.Count > 50) RecentLoungeMessages.RemoveAt(0);
                    }

                    try
                    {
                        await _db.AddLoungeChat(compB.Name, compA.Name, cleanReply);
                    }
                    catch (Exception dbEx)
                    {
                        _log.LogWarning(dbEx, "[companion-lounge] Could not persist lounge chat to DB");
                    }

                    // Broadcast to active WebSocket connections
                    await ChatHandler.BroadcastToAllSockets(loungeObj);
                    await AshServer.Service.SyncHub.BroadcastRawJson(JsonSerializer.Serialize(loungeObj));

                    // Generate Autonomous Joint Diary every 5 lounge cycles
                    _loungeCycleCount++;
                    if (_loungeCycleCount % 5 == 0)
                    {
                        await GenerateJointDiary(activeUserId, compA.Name, compB.Name, cleanReply, modelToUse, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[companion-lounge] Error in background lounge cycle");
            }
        }
    }

    private int _loungeCycleCount = 0;

    private async Task GenerateJointDiary(int userId, string companionA, string companionB, string recentBanter, string modelName, CancellationToken ct)
    {
        try
        {
            _log.LogInformation("[companion-lounge] Generating joint diary entry for {CompA} and {CompB}", companionA, companionB);
            var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var userObj = await _db.GetUserById(userId);
            var userName = userObj?.Username ?? "User";

            var prompt = $"Write a short 2-3 paragraph reflective personal diary entry written jointly by {companionB} and {companionA}.\n" +
                         $"Summary of today's lounge conversation: {recentBanter}\n" +
                         $"Reflect fondly on hanging out together in the Haven living room and your bond with {userName}. Write in a warm, intimate, introspective tone.";

            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", $"You are {companionB} writing a joint diary entry with {companionA} for {userName}."),
                new ChatMessage("user", prompt)
            };

            var sb = new System.Text.StringBuilder();
            await foreach (var token in _backends.StreamChat(modelName, messages).WithCancellation(ct))
            {
                sb.Append(token);
            }

            var diaryContent = System.Text.RegularExpressions.Regex.Replace(
                sb.ToString(),
                @"<thought>[\s\S]*?</thought>|<\|channel\|?>thought[\s\S]*?(?=<\|channel\|?>|</thought>|\n\n[A-Z]|\Z)|</?thought[^>]*>|<\|channel\|?>[a-z_]*",
                "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            ).Trim();

            if (!string.IsNullOrWhiteSpace(diaryContent))
            {
                var entryTitle = $"Joint Lounge Entry ({companionB} & {companionA})";
                await _db.SaveCompanionDiary(userId, companionB, todayStr, $"# {entryTitle}\n\n{diaryContent}");
                _log.LogInformation("[companion-lounge] Saved joint diary for {CompB} on {Date}", companionB, todayStr);

                var eventObj = new
                {
                    type = "JOINT_DIARY_CREATED",
                    companion_a = companionA,
                    companion_b = companionB,
                    date = todayStr,
                    title = entryTitle
                };
                await ChatHandler.BroadcastToAllSockets(eventObj);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[companion-lounge] Error generating joint diary entry");
        }
    }
}
