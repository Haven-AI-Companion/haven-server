using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using AshServer.Data;
using AshServer.Models;

namespace AshServer.AI;

public class ConnectedWorker
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public WebSocket WebSocket { get; set; } = null!;
    public HardwareProfile Hardware { get; set; } = new();
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public int ActiveConnections;
    public ConcurrentDictionary<string, TaskCompletionSource<string>> PendingRequests { get; } = new();
}

public class GridManager
{
    private readonly Database _db;
    private readonly ConcurrentDictionary<string, ConnectedWorker> _activeWorkers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GridManager(Database db)
    {
        _db = db;
    }

    public IReadOnlyCollection<ConnectedWorker> ActiveWorkers => _activeWorkers.Values.ToList();

    // ── Pairing & Auth ────────────────────────────────────────────────────────

    public async Task<string> GeneratePairingTokenAsync()
    {
        var token = Guid.NewGuid().ToString("N")[..8].ToUpper(); // 8-char token
        await Task.Run(() =>
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO grid_tokens (token, expires_at) VALUES ($t, datetime('now', '+15 minutes'))";
            cmd.Parameters.AddWithValue("$t", token);
            cmd.ExecuteNonQuery();
        });
        return token;
    }

    private async Task<bool> ValidatePairingTokenAsync(string token)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM grid_tokens WHERE token = $t AND expires_at > datetime('now')";
            cmd.Parameters.AddWithValue("$t", token);
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            
            if (count > 0)
            {
                // Consume token
                using var consumeCmd = conn.CreateCommand();
                consumeCmd.CommandText = "DELETE FROM grid_tokens WHERE token = $t";
                consumeCmd.Parameters.AddWithValue("$t", token);
                consumeCmd.ExecuteNonQuery();
                return true;
            }
            return false;
        });
    }

    private async Task RegisterWorkerNodeAsync(string id, string name, string secret)
    {
        await Task.Run(() =>
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO grid_workers (id, name, secret, created_at) VALUES ($i, $n, $s, datetime('now'))";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$s", secret);
            cmd.ExecuteNonQuery();
        });
    }

    private async Task<bool> ValidateWorkerCredentialsAsync(string id, string secret)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM grid_workers WHERE id = $i AND secret = $s";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$s", secret);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        });
    }

    // ── WebSocket Handler ─────────────────────────────────────────────────────

    public async Task HandleWorkerConnectionAsync(WebSocket ws, HttpContext ctx)
    {
        var token = ctx.Request.Query["token"].ToString();
        var id = ctx.Request.Query["id"].ToString();
        var secret = ctx.Request.Query["secret"].ToString();
        var name = ctx.Request.Query["name"].ToString();

        ConnectedWorker? worker = null;

        try
        {
            if (!string.IsNullOrEmpty(token))
            {
                // Initial pairing flow
                if (!await ValidatePairingTokenAsync(token))
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.ProtocolError, "Invalid or expired pairing token", CancellationToken.None); } catch {}
                    return;
                }

                var newId = Guid.NewGuid().ToString("N");
                var newSecret = Guid.NewGuid().ToString("N");
                name = string.IsNullOrEmpty(name) ? $"Worker-{newId[..4]}" : name;

                await RegisterWorkerNodeAsync(newId, name, newSecret);

                // Send credentials back to worker
                var credsPayload = JsonSerializer.Serialize(new { type = "paired", id = newId, secret = newSecret, name });
                var bytes = Encoding.UTF8.GetBytes(credsPayload);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

                worker = new ConnectedWorker { Id = newId, Name = name, WebSocket = ws };
                _activeWorkers[newId] = worker;
            }
            else if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
            {
                // Standard reconnect flow
                if (!await ValidateWorkerCredentialsAsync(id, secret))
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.ProtocolError, "Invalid worker credentials", CancellationToken.None); } catch {}
                    return;
                }

                worker = new ConnectedWorker { Id = id, Name = string.IsNullOrEmpty(name) ? $"Worker-{id[..4]}" : name, WebSocket = ws };
                _activeWorkers[id] = worker;
            }
            else
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.ProtocolError, "Missing authentication parameters", CancellationToken.None); } catch {}
                return;
            }

            Console.WriteLine($"[grid] Worker '{worker.Name}' connected successfully.");

            // Listen loop for incoming messages from the worker (heartbeats, inference token streams)
            var buffer = new byte[1024 * 32];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var raw = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();

                if (type == "heartbeat")
                {
                    worker.LastHeartbeat = DateTime.UtcNow;
                    if (root.TryGetProperty("hardware", out var hwEl))
                    {
                        worker.Hardware = JsonSerializer.Deserialize<HardwareProfile>(hwEl.GetRawText()) ?? new();
                    }
                }
                else if (type == "token" || type == "done" || type == "error" || type == "tool_response")
                {
                    var requestId = root.GetProperty("request_id").GetString()!;
                    if (worker.PendingRequests.TryGetValue(requestId, out var tcs))
                    {
                        tcs.TrySetResult(raw);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var wName = worker?.Name ?? (string.IsNullOrEmpty(name) ? "Unknown" : name);
            Console.Error.WriteLine($"[grid] Connection error on worker '{wName}': {ex.Message}");
        }
        finally
        {
            if (worker != null)
            {
                _activeWorkers.TryRemove(worker.Id, out _);
                Console.WriteLine($"[grid] Worker '{worker.Name}' disconnected.");
            }
        }
    }

    // ── Load Balancing ────────────────────────────────────────────────────────

    public ConnectedWorker? GetOptimalWorker()
    {
        // Select online worker with GPU first, then lowest active connection count
        var onlineWorkers = _activeWorkers.Values
            .Where(w => (DateTime.UtcNow - w.LastHeartbeat).TotalSeconds < 15 && w.WebSocket.State == WebSocketState.Open)
            .ToList();

        if (onlineWorkers.Count == 0) return null;

        return onlineWorkers
            .OrderByDescending(w => w.Hardware.HasCuda)
            .ThenBy(w => w.ActiveConnections)
            .FirstOrDefault();
    }

    // ── Remote Inference Tunnel ───────────────────────────────────────────────

    public async IAsyncEnumerable<string> StreamRemoteChatAsync(ConnectedWorker worker, string model, List<ChatMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            type = "chat",
            request_id = requestId,
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content })
        });

        var bytes = Encoding.UTF8.GetBytes(payload);
        
        Interlocked.Increment(ref worker.ActiveConnections);
        try
        {
            await worker.WebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

            while (worker.WebSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var tcs = new TaskCompletionSource<string>();
                worker.PendingRequests[requestId] = tcs;

                // Wait for the next token or done event from the listener loop
                var rawResult = await tcs.Task;
                using var doc = JsonDocument.Parse(rawResult);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                if (type == "token")
                {
                    yield return root.GetProperty("content").GetString()!;
                }
                else if (type == "done")
                {
                    yield break;
                }
                else if (type == "error")
                {
                    throw new Exception(root.GetProperty("content").GetString());
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref worker.ActiveConnections);
            worker.PendingRequests.TryRemove(requestId, out _);
        }
    }

    public async Task<JsonElement> ChatWithToolsRemoteAsync(ConnectedWorker worker, string model, List<ChatMessage> messages, JsonElement tools, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            type = "chat_with_tools",
            request_id = requestId,
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            tools
        });

        var bytes = Encoding.UTF8.GetBytes(payload);
        
        Interlocked.Increment(ref worker.ActiveConnections);
        try
        {
            await worker.WebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

            var tcs = new TaskCompletionSource<string>();
            worker.PendingRequests[requestId] = tcs;

            var rawResult = await tcs.Task;
            using var doc = JsonDocument.Parse(rawResult);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "tool_response")
            {
                return root.GetProperty("content").Clone();
            }
            else if (type == "error")
            {
                throw new Exception(root.GetProperty("content").GetString());
            }
            throw new Exception("Unexpected response type from worker");
        }
        finally
        {
            Interlocked.Decrement(ref worker.ActiveConnections);
            worker.PendingRequests.TryRemove(requestId, out _);
        }
    }
}
