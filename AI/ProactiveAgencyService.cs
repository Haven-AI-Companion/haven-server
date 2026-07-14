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
    private readonly IConfiguration _config;
    private readonly ILogger<ProactiveAgencyService> _log;

    private string _lastActiveWindow = "";
    private readonly Dictionary<string, DateTime> _nextAllowedMessageTime = new();

    public ProactiveAgencyService(
        Database db,
        BackendManager backends,
        PersonalityLoader personality,
        IConfiguration config,
        ILogger<ProactiveAgencyService> log)
    {
        _db = db;
        _backends = backends;
        _personality = personality;
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
                if (activeSockets.IsEmpty)
                {
                    continue; // Nobody connected
                }

                foreach (var convId in activeSockets.Keys)
                {
                    if (!activeSockets.TryGetValue(convId, out var bag) || bag.IsEmpty)
                        continue;

                    var now = DateTime.UtcNow;

                    // Dynamic per-conversation cooldown check
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
                    // We check if it's been at least 4 minutes since the last allowed message time
                    var hasTimePassed = !_nextAllowedMessageTime.TryGetValue(convId, out var lastTime) || (now - lastTime >= TimeSpan.FromMinutes(4));
                    if (!shouldTrigger && hasTimePassed && idleSec < 120)
                    {
                        shouldTrigger = true;
                    }

                    if (!shouldTrigger)
                    {
                        continue;
                    }

                    _log.LogInformation("[proactive-agency] Triggering proactive check for conversation {ConvId} (Active window: {Window})", convId, activeTitle);

                    // Build proactive prompt with explicit volition instructions
                    var companionName = _personality.AiName ?? "Companion";
                    var systemPrompt = _personality.GetSystemPrompt("admin");

                    var messages = new List<ChatMessage>
                    {
                        new("system", systemPrompt),
                        new("system", $"[SYSTEM TELEMETRY TICK]\n" +
                                      $" Daniel's active desktop window: \"{activeTitle}\"\n" +
                                      $" System idle time: {Math.Round(idleSec)} seconds.\n\n" +
                                      $"First, analyze Daniel's state in a <thought>...</thought> tag. Then, decide your next action:\n" +
                                      $"- If you want to stay silent and let Daniel focus, write \"[ACTION]: SILENT\".\n" +
                                      $"- If you want to proactively speak to Daniel, write \"[ACTION]: SPEAK\" followed by your message to Daniel (in character as {companionName}, keep it under 2 sentences, and address Daniel by name).\n\n" +
                                      $"Examples:\n" +
                                      $"<thought>Daniel is busy coding in VS Code. I shouldn't bother him.</thought>\n" +
                                      $"[ACTION]: SILENT\n\n" +
                                      $"<thought>Daniel has been idle for a bit. I'll check in on him.</thought>\n" +
                                      $"[ACTION]: SPEAK Hey Daniel, taking a break? How's your project going?</thought>")
                    };

                    var (backend, modelName) = await _backends.Resolve("default");
                    var responseText = "";

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await foreach (var token in backend.StreamChat(modelName, messages, cts.Token))
                    {
                        responseText += token;
                    }

                    responseText = responseText.Trim();

                    // Parse the thought for debugging/logging
                    var thoughtMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"<thought>([\s\S]*?)</thought>");
                    var innerThought = thoughtMatch.Success ? thoughtMatch.Groups[1].Value.Trim() : "No clear reasoning.";

                    // Parse the action from the response
                    string speakMessage = "";
                    var actionIndex = responseText.IndexOf("[ACTION]:", StringComparison.OrdinalIgnoreCase);
                    if (actionIndex >= 0)
                    {
                        var actionText = responseText.Substring(actionIndex + 9).Trim();
                        if (actionText.StartsWith("SPEAK", StringComparison.OrdinalIgnoreCase))
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
                                speakMessage = rest.Replace("[ACTION]:", "").Replace("SPEAK", "").Trim().TrimStart(':', ' ');
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

                    _log.LogInformation("[proactive-agency] Companion decided to SPEAK. Thought: \"{Thought}\" | Message: {Message}", innerThought, speakMessage);

                    // Add a longer dynamic cooldown (5 to 10 minutes) if they decide to speak to prevent spamming
                    _nextAllowedMessageTime[convId] = now.AddMinutes(Random.Shared.NextDouble() * 5.0 + 5.0);

                    // Save to database
                    await _db.AddMessage(convId, "assistant", speakMessage);

                    // Broadcast to active WebSocket clients
                    await ChatHandler.BroadcastToConversation(convId, new { type = "token", content = speakMessage });
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
}
