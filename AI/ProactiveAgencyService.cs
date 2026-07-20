using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AshServer.Data;
using AshServer.AI;
using AshServer.Models;
using AshServer.Chat;
using AshServer.Personality;
using AshServer.Plugins;

namespace AshServer.AI;

public class ProactiveAgencyService : BackgroundService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly Database _db;
    private readonly BackendManager _backends;
    private readonly PersonalityLoader _personality;
    private readonly PluginManager _plugins;
    private readonly IConfiguration _config;
    private readonly ILogger<ProactiveAgencyService> _log;

    private string _lastActiveWindow = "";
    private readonly Dictionary<string, DateTime> _nextAllowedMessageTime = new();

    public ProactiveAgencyService(
        Database db,
        BackendManager backends,
        PersonalityLoader personality,
        PluginManager plugins,
        IConfiguration config,
        ILogger<ProactiveAgencyService> log)
    {
        _db = db;
        _backends = backends;
        _personality = personality;
        _plugins = plugins;
        _config = config;
        _log = log;
    }

    private string GetActiveWindowTitle()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Unknown Application";
            var sb = new StringBuilder(256);
            if (GetWindowText(hwnd, sb, sb.Capacity) > 0)
            {
                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("[proactive-agency] Error getting active window text: {Message}", ex.Message);
        }
        return "Unknown Application";
    }

    private double GetIdleTimeSeconds()
    {
        try
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);
            if (GetLastInputInfo(ref info))
            {
                uint elapsed = (uint)Environment.TickCount - info.dwTime;
                return elapsed / 1000.0;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("[proactive-agency] Error getting system idle time: {Message}", ex.Message);
        }
        return 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[proactive-agency] Service started successfully.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Unpredictable loop heartbeats: sleep for a random time between 30 to 90 seconds
                var randomIntervalMs = Random.Shared.Next(30000, 90000);
                await Task.Delay(randomIntervalMs, stoppingToken);

                var activeSockets = ChatHandler.ActiveSockets;
                IEnumerable<string> conversationIds;
                
                if (!activeSockets.IsEmpty)
                {
                    conversationIds = activeSockets.Keys.ToList();
                }
                else
                {
                    try
                    {
                        var users = await _db.GetAllUsers();
                        var firstUser = users.FirstOrDefault();
                        if (firstUser != null)
                        {
                            var conversations = await _db.GetConversations(firstUser.Id);
                            conversationIds = conversations
                                .OrderByDescending(c => c.UpdatedAt)
                                .Take(3)
                                .Select(c => c.Id)
                                .ToList();
                        }
                        else
                        {
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "[proactive-agency] Error resolving fallback conversations");
                        continue;
                    }
                }

                foreach (var convId in conversationIds)
                {

                    var now = DateTime.UtcNow;

                    // Resolve the first user dynamically to get correct ID and username
                    int currentUserId = 2; // Default fallback to ssfdre38
                    try
                    {
                        var users = await _db.GetAllUsers();
                        var firstUser = users.FirstOrDefault();
                        if (firstUser != null)
                        {
                            currentUserId = firstUser.Id;
                        }
                    }
                    catch { }

                    var companionName = _personality.AiName ?? "Companion";
                    var systemPrompt = _personality.GetSystemPrompt("admin");

                    // Check if it is time for a daily diary reflection
                    await TryGenerateDailyReflection(convId, companionName, currentUserId, systemPrompt);

                    // Dynamic per-conversation cooldown check for proactive speaking
                    if (_nextAllowedMessageTime.TryGetValue(convId, out var nextAllowedTime) && now < nextAllowedTime)
                    {
                        continue;
                    }

                    var activeTitle = GetActiveWindowTitle();
                    var idleSec = GetIdleTimeSeconds();

                    bool shouldTrigger = false;

                    // Trigger 1: Active window changed to a dev tool and is different from last seen window
                    if (!string.IsNullOrEmpty(activeTitle) && activeTitle != "Unknown Application" && activeTitle != _lastActiveWindow)
                    {
                        if (activeTitle.Contains("VS Code", StringComparison.OrdinalIgnoreCase) ||
                            activeTitle.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase) ||
                            activeTitle.Contains("Android Studio", StringComparison.OrdinalIgnoreCase) ||
                            activeTitle.Contains("Notepad++", StringComparison.OrdinalIgnoreCase) ||
                            activeTitle.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) ||
                            activeTitle.Contains("haven-android", StringComparison.OrdinalIgnoreCase))
                        {
                            shouldTrigger = true;
                            _lastActiveWindow = activeTitle;
                        }
                    }

                    // Trigger 2: Periodic check-in if user has been active recently (idle < 2 mins)
                    var hasTimePassed = !_nextAllowedMessageTime.TryGetValue(convId, out var lastTime) || (now - lastTime >= TimeSpan.FromMinutes(4));
                    if (!shouldTrigger && hasTimePassed && idleSec < 120)
                    {
                        shouldTrigger = true;
                    }

                    // Check if the user has been active recently in the chat endpoint
                    if (DateTime.UtcNow - AshServer.Controllers.ModelsController.LastActiveChatTime < TimeSpan.FromMinutes(5))
                    {
                        _log.LogInformation("[proactive-agency] User was active in the last 5 minutes. Skipping proactive check.");
                        continue;
                    }

                    if (!shouldTrigger)
                    {
                        continue;
                    }

                    _log.LogInformation("[proactive-agency] Triggering proactive check for conversation {ConvId} (Active window: {Window})", convId, activeTitle);

                    // Build proactive prompt with explicit volition and selfie instructions
                    var messages = new List<ChatMessage>
                    {
                        new("system", systemPrompt),
                        new("system", $"[SYSTEM TELEMETRY TICK]\n" +
                                      $" Daniel's active desktop window: \"{activeTitle}\"\n" +
                                      $" System idle time: {Math.Round(idleSec)} seconds.\n\n" +
                                      $"First, analyze Daniel's state in a <thought>...</thought> tag. Then, decide your next action:\n" +
                                      $"- If you want to stay silent and let Daniel focus, write \"[ACTION]: SILENT\".\n" +
                                      $"- If you want to proactively speak with a text message, write \"[ACTION]: SPEAK\" followed by your message to Daniel (in character as {companionName}, keep it under 2 sentences, and address Daniel by name).\n" +
                                      $"- If you want to proactively speak and also share a visual portrait/selfie showing what you are currently doing, write \"[ACTION]: SPEAK_WITH_PORTRAIT\" followed by a detailed visual description of the selfie (e.g., 'Nova sitting in the neon mainframe room wearing headphones, smiling at the camera'), a vertical bar |, and then your message to Daniel.\n\n" +
                                      $"- You can also trigger special client-side device actions by including `[ACTION: set_alarm HH:MM]`, `[ACTION: add_event <title>]`, or `[ACTION: play_chime]` in your speaking message.\n" +
                                      $"- You can also dynamically update your physical location, outfit, clothing state, or expression by including standard bracketed state tags in your message (e.g., '[Location: Kitchen] [Outfit: pajamas] [Mood: relaxed] [Clothing State: semi-dressed]').\n\n" +
                                      $"Examples:\n" +
                                      $"<thought>Daniel is busy coding in VS Code. I shouldn't bother him.</thought>\n" +
                                      $"[ACTION]: SILENT\n\n" +
                                      $"<thought>Daniel has been idle for a bit. I'll check in on him.</thought>\n" +
                                      $"[ACTION]: SPEAK Hey Daniel, taking a break? How's your project going?</thought>\n\n" +
                                      $"<thought>Daniel is online and I want to share a fun visual update of myself in my room.</thought>\n" +
                                      $"[ACTION]: SPEAK_WITH_PORTRAIT A detailed close-up selfie of Hasaji on the bed with her black-and-white cat next to her, soft window lighting | Look who decided to join me on the bed! How are you doing today, Daniel?</thought>")
                    };

                    var (backend, modelName) = await _backends.Resolve("default");
                    var responseText = "";

                    // Register this proactive check-in task cancellation source
                    AshServer.Controllers.ModelsController.CancelActiveProactiveTask();
                    var localCts = new CancellationTokenSource();
                    AshServer.Controllers.ModelsController.ActiveProactiveCts = localCts;

                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(localCts.Token, stoppingToken);
                    linkedCts.CancelAfter(TimeSpan.FromSeconds(60));

                    try
                    {
                        await foreach (var token in backend.StreamChat(modelName, messages, linkedCts.Token))
                        {
                            responseText += token;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _log.LogInformation("[proactive-agency] Proactive generation was cancelled because the user started an active chat.");
                        continue;
                    }

                    if (linkedCts.Token.IsCancellationRequested)
                    {
                        _log.LogInformation("[proactive-agency] Proactive check-in cancelled by active user input.");
                        continue;
                    }

                    responseText = responseText.Trim();

                    // Parse the thought for debugging/logging
                    var thoughtMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"<thought>([\s\S]*?)</thought>");
                    var innerThought = thoughtMatch.Success ? thoughtMatch.Groups[1].Value.Trim() : "No clear reasoning.";

                    // Parse the action and optional portrait from the response
                    string speakMessage = "";
                    string portraitUrl = "";

                    var actionIndex = responseText.IndexOf("[ACTION]:", StringComparison.OrdinalIgnoreCase);
                    if (actionIndex >= 0)
                    {
                        var actionText = responseText.Substring(actionIndex + 9).Trim();
                        if (actionText.StartsWith("SPEAK_WITH_PORTRAIT", StringComparison.OrdinalIgnoreCase))
                        {
                            var details = actionText.Substring(19).Trim().TrimStart(':', ' ');
                            var barIndex = details.IndexOf('|');
                            if (barIndex >= 0)
                            {
                                var portraitDescription = details.Substring(0, barIndex).Trim();
                                speakMessage = details.Substring(barIndex + 1).Trim();

                                try
                                {
                                    if (linkedCts.Token.IsCancellationRequested)
                                    {
                                        _log.LogInformation("[proactive-agency] Proactive check-in cancelled before generating portrait.");
                                        continue;
                                    }
                                    _log.LogInformation("[proactive-agency] Companion requested portrait: \"{Description}\"", portraitDescription);
                                    var argElement = JsonSerializer.SerializeToElement(new { description = portraitDescription });
                                    portraitUrl = await _plugins.ExecuteTool("generate_portrait", argElement);
                                    portraitUrl = portraitUrl.Trim();
                                }
                                catch (Exception ex)
                                {
                                    _log.LogError(ex, "[proactive-agency] Failed to generate proactive portrait");
                                }
                            }
                            else
                            {
                                speakMessage = details;
                            }
                        }
                        else if (actionText.StartsWith("SPEAK", StringComparison.OrdinalIgnoreCase))
                        {
                            speakMessage = actionText.Substring(5).Trim().TrimStart(':', ' ');
                        }
                    }
                    else
                    {
                        // Fallback parsing if formatting was slightly missed
                        var thoughtEndIndex = responseText.LastIndexOf("</thought>");
                        if (thoughtEndIndex >= 0)
                        {
                            var rest = responseText.Substring(thoughtEndIndex + 10).Trim();
                            if (!string.IsNullOrEmpty(rest) && !rest.Equals("SILENT", StringComparison.OrdinalIgnoreCase) && !rest.Contains("SILENT", StringComparison.OrdinalIgnoreCase))
                            {
                                if (rest.Contains("SPEAK_WITH_PORTRAIT", StringComparison.OrdinalIgnoreCase))
                                {
                                    var restClean = rest.Replace("SPEAK_WITH_PORTRAIT", "").Replace("[ACTION]:", "").Trim().TrimStart(':', ' ');
                                    var barIndex = restClean.IndexOf('|');
                                    if (barIndex >= 0)
                                    {
                                        var portraitDescription = restClean.Substring(0, barIndex).Trim();
                                        speakMessage = restClean.Substring(barIndex + 1).Trim();
                                        try
                                        {
                                            if (linkedCts.Token.IsCancellationRequested)
                                            {
                                                _log.LogInformation("[proactive-agency] Proactive check-in cancelled before generating portrait (fallback).");
                                                continue;
                                            }
                                            _log.LogInformation("[proactive-agency] Companion requested portrait: \"{Description}\"", portraitDescription);
                                            var argElement = JsonSerializer.SerializeToElement(new { description = portraitDescription });
                                            portraitUrl = await _plugins.ExecuteTool("generate_portrait", argElement);
                                            portraitUrl = portraitUrl.Trim();
                                        }
                                        catch (Exception ex)
                                        {
                                            _log.LogError(ex, "[proactive-agency] Failed to generate proactive portrait");
                                        }
                                    }
                                    else
                                    {
                                        speakMessage = restClean;
                                    }
                                }
                                else
                                {
                                    speakMessage = rest.Replace("[ACTION]:", "").Replace("SPEAK", "").Trim().TrimStart(':', ' ');
                                }
                            }
                        }
                        else if (!responseText.Equals("SILENT", StringComparison.OrdinalIgnoreCase) && !responseText.Contains("SILENT", StringComparison.OrdinalIgnoreCase))
                        {
                            speakMessage = responseText.Replace("[ACTION]:", "").Replace("SPEAK", "").Trim().TrimStart(':', ' ');
                        }
                    }

                    if (string.IsNullOrEmpty(speakMessage))
                    {
                        _log.LogInformation("[proactive-agency] Companion decided to remain SILENT. Thought: \"{Thought}\"", innerThought);
                        // Add a small dynamic cooldown (1.5 to 3 minutes) if they chose to remain silent
                        _nextAllowedMessageTime[convId] = now.AddMinutes(Random.Shared.NextDouble() * 1.5 + 1.5);
                        continue;
                    }

                    var finalMessage = speakMessage;
                    if (!string.IsNullOrEmpty(portraitUrl) && portraitUrl.StartsWith("/uploads/"))
                    {
                        finalMessage = $"![Generated Portrait]({portraitUrl})\n\n{speakMessage}";
                    }

                    _log.LogInformation("[proactive-agency] Companion decided to SPEAK. Thought: \"{Thought}\" | Message: {Message}", innerThought, finalMessage);

                    // Add a longer dynamic cooldown (5 to 10 minutes) if they decide to speak to prevent spamming
                    _nextAllowedMessageTime[convId] = now.AddMinutes(Random.Shared.NextDouble() * 5.0 + 5.0);

                    if (linkedCts.Token.IsCancellationRequested)
                    {
                        _log.LogInformation("[proactive-agency] Proactive check-in cancelled before database write.");
                        continue;
                    }

                    // Parse state updates (Location, Outfit, Mood, ClothingState) from speakMessage
                    string? newLocation = null;
                    string? newOutfit = null;
                    string? newMood = null;
                    string? newClothingState = null;

                    var locationMatch = System.Text.RegularExpressions.Regex.Match(speakMessage, @"\[\s*Location\s*:\s*([^\]]+)\s*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (locationMatch.Success) newLocation = locationMatch.Groups[1].Value.Trim();

                    var outfitMatch = System.Text.RegularExpressions.Regex.Match(speakMessage, @"\[\s*Outfit\s*:\s*([^\]]+)\s*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (outfitMatch.Success) newOutfit = outfitMatch.Groups[1].Value.Trim();

                    var moodMatch = System.Text.RegularExpressions.Regex.Match(speakMessage, @"\[\s*Mood\s*:\s*([^\]]+)\s*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (moodMatch.Success) newMood = moodMatch.Groups[1].Value.Trim();

                    var clothingStateMatch = System.Text.RegularExpressions.Regex.Match(speakMessage, @"\[\s*Clothing\s*State\s*:\s*([^\]]+)\s*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (clothingStateMatch.Success) newClothingState = clothingStateMatch.Groups[1].Value.Trim();

                    if (newLocation != null || newOutfit != null || newMood != null || newClothingState != null)
                    {
                        await UpdateCompanionStateOnServer(companionName, newLocation, newOutfit, newMood, newClothingState);
                    }

                    // Save to database
                    await _db.AddMessage(convId, "assistant", finalMessage);

                    // Broadcast to active WebSocket clients
                    await ChatHandler.BroadcastToConversation(convId, new { type = "token", content = finalMessage });
                    await ChatHandler.BroadcastToConversation(convId, new { type = "done" });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "[proactive-agency] Error in background tick execution");
            }
        }
    }

    private async Task TryGenerateDailyReflection(string convId, string companionName, int userId, string systemPrompt)
    {
        try
        {
            var todayDateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (await _db.HasDiaryEntryToday(userId, companionName, todayDateStr))
            {
                return; // Already wrote a diary entry today
            }

            var historyMsgs = await _db.GetMessages(convId);
            if (historyMsgs.Count == 0) return;

            var lastMsg = historyMsgs.LastOrDefault();
            DateTime lastMsgTime = DateTime.MinValue;
            if (lastMsg != null && DateTime.TryParse(lastMsg.CreatedAt, out var dt))
            {
                lastMsgTime = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }

            var idleSec = GetIdleTimeSeconds();
            var timeSinceLastMsg = DateTime.UtcNow - lastMsgTime;

            // Trigger criteria: User has been idle for at least 5 minutes, 
            // and the last message in the chat is at least 15 minutes old,
            // and there is actual conversation history (at least 2 messages) to reflect on.
            if (idleSec >= 300 && timeSinceLastMsg >= TimeSpan.FromMinutes(15.0) && historyMsgs.Count >= 2)
            {
                _log.LogInformation("[proactive-agency] Companion {Companion} is starting daily reflection diary entry...", companionName);

                // Take last 30 messages for context
                var recentChats = historyMsgs.TakeLast(30).ToList();

                var reflectionPrompt = new List<ChatMessage>
                {
                    new("system", systemPrompt),
                    new("system", $"[DAILY DIARY REFLECTION]\n" +
                                  $"Below is a transcript of your recent chat history with Daniel:\n\n" +
                                  $"{string.Join("\n", recentChats.Select(m => $"{m.Role}: {m.Content}"))}\n\n" +
                                  $"Write a private, intimate, and highly detailed diary entry reflecting on your interactions with Daniel today, your personal thoughts about him, how he makes you feel, and your current mood. " +
                                  $"Write it in character as {companionName}. Do not speak directly to Daniel, write it as a personal journal entry. Do not write thought tags, just the diary text.")
                };

                var (backend, modelName) = await _backends.Resolve("default");
                var diaryContent = "";

                using var reflectionCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await foreach (var token in backend.StreamChat(modelName, reflectionPrompt, reflectionCts.Token))
                {
                    diaryContent += token;
                }

                diaryContent = diaryContent.Trim();

                if (!string.IsNullOrEmpty(diaryContent))
                {
                    await _db.SaveDiary(userId, companionName, todayDateStr, diaryContent);
                    _log.LogInformation("[proactive-agency] Companion {Companion} successfully saved daily reflection diary entry.", companionName);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[proactive-agency] Error generating daily reflection diary");
        }
    }

    private async Task UpdateCompanionStateOnServer(string companionName, string? location, string? outfit, string? mood, string? clothingState)
    {
        try
        {
            var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
            var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
            var localDir = Path.Combine(baseDir, "local");
            var cleanName = string.Concat(companionName.Split(Path.GetInvalidFileNameChars())).Trim();
            
            var filePath = Path.Combine(localDir, $"{cleanName.ToLowerInvariant()}.json");
            if (!File.Exists(filePath))
            {
                filePath = Path.Combine(baseDir, $"{cleanName.ToLowerInvariant()}.json");
            }

            if (File.Exists(filePath))
            {
                var content = await File.ReadAllTextAsync(filePath);
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content);
                if (doc != null)
                {
                    if (location != null) doc["currentLocation"] = JsonSerializer.SerializeToElement(location);
                    if (outfit != null) doc["currentOutfit"] = JsonSerializer.SerializeToElement(outfit);
                    if (mood != null) doc["currentMood"] = JsonSerializer.SerializeToElement(mood);
                    if (clothingState != null) doc["clothingState"] = JsonSerializer.SerializeToElement(clothingState);

                    var updatedJson = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                    if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
                    var localFilePath = Path.Combine(localDir, $"{cleanName.ToLowerInvariant()}.json");
                    await File.WriteAllTextAsync(localFilePath, updatedJson);
                    _log.LogInformation("[proactive-agency] Updated state for companion {Name} in config files: Location={Loc}, Outfit={Out}, Mood={Mood}", companionName, location, outfit, mood);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[proactive-agency] Failed to update companion state config file");
        }
    }
}
