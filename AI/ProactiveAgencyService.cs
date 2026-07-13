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
    private DateTime _lastMessageTime = DateTime.MinValue;

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

        // Loop runs every 45 seconds to keep it responsive but light
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(45000, stoppingToken);

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
                    if (now - _lastMessageTime < TimeSpan.FromMinutes(2.5))
                    {
                        continue; // Strict cooldown of 2.5 minutes between any proactive checks
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
                    if (!shouldTrigger && now - _lastMessageTime >= TimeSpan.FromMinutes(5) && idleSec < 120)
                    {
                        shouldTrigger = true;
                    }

                    if (!shouldTrigger)
                    {
                        continue;
                    }

                    _log.LogInformation("[proactive-agency] Triggering proactive check for conversation {ConvId} (Active window: {Window})", convId, activeTitle);

                    // Build proactive prompt
                    var companionName = _personality.AiName ?? "Companion";
                    var systemPrompt = _personality.GetSystemPrompt("admin");

                    var messages = new List<ChatMessage>
                    {
                        new("system", systemPrompt),
                        new("system", $"[SYSTEM TELEMETRY TICK]\n" +
                                      $" Daniel's active desktop window: \"{activeTitle}\"\n" +
                                      $" System idle time: {Math.Round(idleSec)} seconds.\n\n" +
                                      $"Decide your next action. You can:\n" +
                                      $"1. Stay silent and let Daniel focus. To do this, reply with ONLY the exact word \"SILENT\". Do not write thought tags or any other text.\n" +
                                      $"2. Proactively text Daniel. Speak in character as {companionName}, keep it under 2 sentences, and address Daniel by name. Do not output thought tags, just the message.\n" +
                                      $"If Daniel is busy coding, comment on his work or encourage him. If he is idle, check in on him.")
                    };

                    var (backend, modelName) = await _backends.Resolve("default");
                    var responseText = "";

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await foreach (var token in backend.StreamChat(modelName, messages, cts.Token))
                    {
                        responseText += token;
                    }

                    responseText = responseText.Trim();

                    if (string.IsNullOrEmpty(responseText) || responseText.Equals("SILENT", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.LogInformation("[proactive-agency] Companion decided to remain SILENT.");
                        continue;
                    }

                    _log.LogInformation("[proactive-agency] Companion decided to SPEAK: {Message}", responseText);

                    // Update last message time to prevent spam
                    _lastMessageTime = DateTime.UtcNow;

                    // Save to database
                    await _db.AddMessage(convId, "assistant", responseText);

                    // Broadcast to active WebSocket clients
                    await ChatHandler.BroadcastToConversation(convId, new { type = "token", content = responseText });
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
