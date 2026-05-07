using System.Text.Json;
using AshServer.Models;

namespace AshServer.Mcp;

/// <summary>Manages all configured MCP servers and exposes their tools to the agent.</summary>
public class McpManager : IAsyncDisposable
{
    private readonly List<McpClient>   _clients = [];
    private readonly IConfiguration    _config;
    private readonly ILogger<McpManager> _logger;

    public McpManager(IConfiguration config, ILogger<McpManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var servers = _config.GetSection("Mcp:Servers").Get<List<McpServerConfig>>() ?? [];
        if (servers.Count == 0)
        {
            _logger.LogInformation("[mcp] no servers configured (add Mcp:Servers to appsettings.json)");
            return;
        }

        foreach (var server in servers.Where(s => s.Enabled))
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
    }

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
