using System.Text.Json;
using AshServer.Data;
using AshServer.Models;

namespace AshServer.Mcp;

/// <summary>Manages all configured MCP servers and exposes their tools to the agent.</summary>
public class McpManager : IAsyncDisposable
{
    private readonly List<McpClient>     _clients = [];
    private readonly Database            _db;
    private readonly ILogger<McpManager> _logger;

    public McpManager(Database db, ILogger<McpManager> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var servers = await _db.GetMcpServers();
        if (servers.Count == 0)
        {
            _logger.LogInformation("[mcp] no servers configured");
            return;
        }

        foreach (var server in servers.Where(s => s.Enabled))
            await ConnectInternalAsync(server, ct);
    }

    private async Task ConnectInternalAsync(McpServerConfig server, CancellationToken ct)
    {
        var client = new McpClient(server);
        _clients.Add(client);
        try
        {
            await client.ConnectAsync(ct);
            if (client.Connected)
                _logger.LogInformation("[mcp] '{Id}' connected ({Count} tools)", server.Id, client.Tools.Count);
            else
                _logger.LogWarning("[mcp] '{Id}' failed to connect: {Error}", server.Id, client.LastError);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[mcp] '{Id}' exception during connect: {Ex}", server.Id, ex.Message);
        }
    }

    // ── CRUD helpers called by McpController ──────────────────────────────

    /// <summary>Saves a new server to DB, connects it, and returns connection status.</summary>
    public async Task<bool> AddServerAsync(McpServerConfig config, CancellationToken ct = default)
    {
        await _db.CreateMcpServer(config);
        if (!config.Enabled) return false;
        await ConnectInternalAsync(config, ct);
        return _clients.FirstOrDefault(c => c.Config.Id == config.Id)?.Connected ?? false;
    }

    /// <summary>Persists updated config, reconnects if enabled.</summary>
    public async Task<bool> UpdateServerAsync(McpServerConfig config, CancellationToken ct = default)
    {
        await _db.UpdateMcpServer(config);
        await DisconnectAsync(config.Id);
        if (!config.Enabled) return false;
        await ConnectInternalAsync(config, ct);
        return _clients.FirstOrDefault(c => c.Config.Id == config.Id)?.Connected ?? false;
    }

    /// <summary>Removes from DB and disconnects.</summary>
    public async Task DeleteServerAsync(string id)
    {
        await _db.DeleteMcpServer(id);
        await DisconnectAsync(id);
    }

    /// <summary>Toggles enabled flag; connects or disconnects accordingly.</summary>
    public async Task<bool> ToggleServerAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await _db.ToggleMcpServer(id, enabled);
        await DisconnectAsync(id);
        if (!enabled) return false;
        var config = await _db.GetMcpServer(id);
        if (config is null) return false;
        await ConnectInternalAsync(config, ct);
        return _clients.FirstOrDefault(c => c.Config.Id == id)?.Connected ?? false;
    }

    /// <summary>Reloads config from DB and reconnects.</summary>
    public async Task<bool> ReconnectAsync(string id, CancellationToken ct = default)
    {
        await DisconnectAsync(id);
        var config = await _db.GetMcpServer(id);
        if (config is null || !config.Enabled) return false;
        await ConnectInternalAsync(config, ct);
        return _clients.FirstOrDefault(c => c.Config.Id == id)?.Connected ?? false;
    }

    private async Task DisconnectAsync(string id)
    {
        var existing = _clients.FirstOrDefault(c => c.Config.Id == id);
        if (existing is null) return;
        _clients.Remove(existing);
        await existing.DisposeAsync();
    }

    // ── Agent integration ─────────────────────────────────────────────────

    /// <summary>Returns all MCP tool definitions merged into OpenAI function-calling format.</summary>
    public JsonElement GetToolDefinitions()
    {
        var tools = _clients
            .Where(c => c.Connected)
            .SelectMany(c => c.Tools)
            .Select(t => (object)new
            {
                type = "function",
                function = new
                {
                    name        = t.Name,
                    description = t.Description,
                    parameters  = t.InputSchema
                }
            })
            .ToList();
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(tools));
    }

    public bool IsMcpTool(string toolName) =>
        _clients.Any(c => c.Connected && c.Tools.Any(t => t.Name == toolName));

    public async Task<string> ExecuteToolAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        var match = _clients
            .Where(c => c.Connected)
            .Select(c => (Client: c, Tool: c.Tools.FirstOrDefault(t => t.Name == toolName)))
            .FirstOrDefault(x => x.Tool is not null);

        if (match.Client is null) return $"MCP tool '{toolName}' not found";
        return await match.Client.CallToolAsync(toolName, args, ct);
    }

    public IReadOnlyList<McpServerInfo> GetServerInfos() =>
        _clients.Select(c => new McpServerInfo(
            c.Config.Id,
            c.Config.Name,
            c.Config.Type,
            c.Connected,
            c.Tools.Count,
            c.Tools.Select(t => t.Name).ToList(),
            c.LastError
        )).ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
    }
}
