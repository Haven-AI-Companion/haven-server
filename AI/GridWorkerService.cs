using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using AshServer.Models;

namespace AshServer.AI;

public class GridWorkerService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly BackendManager _backends;
    private readonly HardwareProfiler _profiler;
    private readonly IHostApplicationLifetime _lifetime;
    private ClientWebSocket? _ws;

    public GridWorkerService(
        IConfiguration config,
        BackendManager backends,
        HardwareProfiler profiler,
        IHostApplicationLifetime lifetime)
    {
        _config = config;
        _backends = backends;
        _profiler = profiler;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var isWorker = _config.GetValue("Mode", "server").Equals("worker", StringComparison.OrdinalIgnoreCase);
        if (!isWorker)
        {
            return;
        }

        Console.WriteLine("[grid-worker] Starting in Worker Mode...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var masterUrl = _config["Grid:MasterUrl"]?.TrimEnd('/');
                if (string.IsNullOrEmpty(masterUrl))
                {
                    Console.WriteLine("[grid-worker] Warning: Grid:MasterUrl is not configured. Retrying in 10 seconds...");
                    await Task.Delay(10000, stoppingToken);
                    continue;
                }

                var wsUriStr = masterUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/api/grid/ws";
                
                var id = _config["Grid:WorkerId"];
                var secret = _config["Grid:WorkerSecret"];
                var name = _config["Grid:WorkerName"] ?? Environment.MachineName;
                var token = _config["Grid:PairingToken"];

                string query;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
                {
                    query = $"?id={id}&secret={secret}&name={Uri.EscapeDataString(name)}";
                }
                else if (!string.IsNullOrEmpty(token))
                {
                    query = $"?token={token}&name={Uri.EscapeDataString(name)}";
                }
                else
                {
                    Console.WriteLine("[grid-worker] Warning: No worker credentials or pairing token configured. Please configure Grid:PairingToken. Retrying in 10 seconds...");
                    await Task.Delay(10000, stoppingToken);
                    continue;
                }

                var uri = new Uri(wsUriStr + query);
                Console.WriteLine($"[grid-worker] Connecting to Master: {wsUriStr}...");

                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                await _ws.ConnectAsync(uri, stoppingToken);
                Console.WriteLine("[grid-worker] Connected to Master successfully.");

                // Start heartbeat loop in background
                var heartbeatTask = RunHeartbeatLoopAsync(_ws, stoppingToken);

                // Start message listener loop
                await RunListenerLoopAsync(_ws, stoppingToken);

                await heartbeatTask;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[grid-worker] Connection lost: {ex.Message}. Reconnecting in 5 seconds...");
                if (_ws != null)
                {
                    try { _ws.Dispose(); } catch { }
                }
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var hwProfile = _profiler.ProfileSystem();
                var heartbeat = JsonSerializer.Serialize(new
                {
                    type = "heartbeat",
                    hardware = hwProfile
                });

                var bytes = Encoding.UTF8.GetBytes(heartbeat);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            catch { }

            await Task.Delay(5000, ct);
        }
    }

    private async Task RunListenerLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var raw = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl)) continue;
            var type = typeEl.GetString();

            if (type == "paired")
            {
                // Pairing successful -> Save credentials to config.json
                var newId = root.GetProperty("id").GetString()!;
                var newSecret = root.GetProperty("secret").GetString()!;
                var newName = root.GetProperty("name").GetString()!;

                await SaveCredentialsAsync(newId, newSecret, newName);
                Console.WriteLine($"[grid-worker] Successfully paired with Master. Registered as '{newName}' (ID: {newId[..4]}).");
            }
            else if (type == "chat")
            {
                var requestId = root.GetProperty("request_id").GetString()!;
                var model = root.GetProperty("model").GetString()!;
                var msgsEl = root.GetProperty("messages");

                var messages = msgsEl.EnumerateArray().Select(m => new ChatMessage(
                    m.GetProperty("role").GetString()!,
                    m.GetProperty("content").GetString()!
                )).ToList();

                // Run inference in a separate task to avoid blocking the listener loop
                _ = Task.Run(() => RunInferenceAsync(ws, requestId, model, messages, ct), ct);
            }
            else if (type == "chat_with_tools")
            {
                var requestId = root.GetProperty("request_id").GetString()!;
                var model = root.GetProperty("model").GetString()!;
                var msgsEl = root.GetProperty("messages");
                var toolsEl = root.GetProperty("tools");

                var messages = msgsEl.EnumerateArray().Select(m => new ChatMessage(
                    m.GetProperty("role").GetString()!,
                    m.GetProperty("content").GetString()!
                )).ToList();

                // Run inference with tools in a separate task
                _ = Task.Run(() => RunInferenceWithToolsAsync(ws, requestId, model, messages, toolsEl.Clone(), ct), ct);
            }
        }
    }

    private async Task RunInferenceAsync(ClientWebSocket ws, string requestId, string model, List<ChatMessage> messages, CancellationToken ct)
    {
        try
        {
            // Resolve local model backend
            var defaultModelId = "default";
            var (backend, modelName) = await _backends.Resolve(defaultModelId);

            await foreach (var token in backend.StreamChat(modelName, messages, ct))
            {
                if (ws.State != WebSocketState.Open) return;

                var payload = JsonSerializer.Serialize(new
                {
                    type = "token",
                    request_id = requestId,
                    content = token
                });
                var bytes = Encoding.UTF8.GetBytes(payload);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }

            // Send done
            if (ws.State == WebSocketState.Open)
            {
                var donePayload = JsonSerializer.Serialize(new { type = "done", request_id = requestId });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(donePayload)), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[grid-worker] Inference failed for request {requestId}: {ex.Message}");
            if (ws.State == WebSocketState.Open)
            {
                var errPayload = JsonSerializer.Serialize(new { type = "error", request_id = requestId, content = ex.Message });
                try { await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(errPayload)), WebSocketMessageType.Text, true, ct); } catch { }
            }
        }
    }

    private async Task RunInferenceWithToolsAsync(ClientWebSocket ws, string requestId, string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct)
    {
        try
        {
            var defaultModelId = "default";
            var (backend, modelName) = await _backends.Resolve(defaultModelId);

            var result = await backend.ChatWithTools(modelName, messages, tools, ct);

            if (ws.State == WebSocketState.Open)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "tool_response",
                    request_id = requestId,
                    content = result
                });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[grid-worker] Tool inference failed for request {requestId}: {ex.Message}");
            if (ws.State == WebSocketState.Open)
            {
                var errPayload = JsonSerializer.Serialize(new { type = "error", request_id = requestId, content = ex.Message });
                try { await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(errPayload)), WebSocketMessageType.Text, true, ct); } catch { }
            }
        }
    }

    private async Task SaveCredentialsAsync(string id, string secret, string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        System.Text.Json.Nodes.JsonObject root;
        if (File.Exists(path))
        {
            try { root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); }
            catch { root = new System.Text.Json.Nodes.JsonObject(); }
        }
        else
        {
            root = new System.Text.Json.Nodes.JsonObject();
        }

        if (!root.ContainsKey("Grid"))
        {
            root["Grid"] = new System.Text.Json.Nodes.JsonObject();
        }

        var gridNode = root["Grid"]!.AsObject();
        gridNode["WorkerId"] = id;
        gridNode["WorkerSecret"] = secret;
        gridNode["WorkerName"] = name;
        gridNode.Remove("PairingToken"); // Remove pairing token since we are paired

        var opts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, root.ToJsonString(opts));
    }
}
