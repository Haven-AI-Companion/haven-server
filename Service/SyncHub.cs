using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AshServer.Service
{
    public class SyncHub
    {
        private static readonly ConcurrentDictionary<string, WebSocket> _clients = new();

        public static void Register(string connectionId, WebSocket socket)
        {
            _clients[connectionId] = socket;
            Console.WriteLine($"[SyncHub] Registered WebSocket client: {connectionId} (Total: {_clients.Count})");
        }

        public static void Unregister(string connectionId)
        {
            if (_clients.TryRemove(connectionId, out _))
            {
                Console.WriteLine($"[SyncHub] Unregistered WebSocket client: {connectionId} (Total: {_clients.Count})");
            }
        }

        public static async Task BroadcastMessage(string conversationId, string companionName, string sender, string content)
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "NEW_MESSAGE",
                conversation_id = conversationId,
                companion_name = companionName,
                sender = sender,
                content = content,
                timestamp = DateTime.UtcNow.ToString("o")
            });

            var bytes = Encoding.UTF8.GetBytes(payload);
            var segment = new ArraySegment<byte>(bytes);

            foreach (var (connId, socket) in _clients)
            {
                if (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SyncHub] Error sending sync to {connId}: {ex.Message}");
                    }
                }
            }
        }

        public static async Task BroadcastRawJson(string jsonPayload)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonPayload);
            var segment = new ArraySegment<byte>(bytes);

            foreach (var (connId, socket) in _clients)
            {
                if (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SyncHub] Error broadcasting to {connId}: {ex.Message}");
                    }
                }
            }
        }

        public static async Task HandleConnection(HttpContext ctx, WebSocket ws)
        {
            var connId = Guid.NewGuid().ToString();
            Register(connId, ws);

            var buffer = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ctx.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", ctx.RequestAborted);
                            }
                        }
                        catch { }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch { }
            finally
            {
                Unregister(connId);
            }
        }
    }
}
