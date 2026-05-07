using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AshServer.Models;

namespace AshServer.Mcp;

/// <summary>Connects to and communicates with a single MCP server (stdio or HTTP JSON-RPC).</summary>
public class McpClient : IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private Process?      _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _nextId;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<McpTool> _tools = [];
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public McpServerConfig       Config    => _config;
    public IReadOnlyList<McpTool> Tools    => _tools;
    public bool                   Connected { get; private set; }
    public string?                LastError { get; private set; }

    public McpClient(McpServerConfig config) => _config = config;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_config.Type == "http")
            await ConnectHttpAsync(ct);
        else
            await ConnectStdioAsync(ct);
    }

    private async Task ConnectHttpAsync(CancellationToken ct)
    {
        try
        {
            await RpcAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities    = new { },
                clientInfo      = new { name = "ash-server", version = "1.0" }
            }, ct);
            await DiscoverToolsAsync(ct);
            Connected = true;
        }
        catch (Exception ex) { LastError = ex.Message; Connected = false; }
    }

    private async Task ConnectStdioAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = _config.Command,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardInputEncoding  = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var arg in _config.Args)    psi.ArgumentList.Add(arg);
            foreach (var (k, v) in _config.Env) psi.Environment[k] = v;

            _process  = Process.Start(psi) ?? throw new Exception("Failed to start MCP process");
            _stdin    = _process.StandardInput;
            _stdout   = _process.StandardOutput;
            _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);

            await RpcStdioAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities    = new { },
                clientInfo      = new { name = "ash-server", version = "1.0" }
            }, ct);

            await SendNotificationAsync("notifications/initialized");
            await DiscoverToolsAsync(ct);
            Connected = true;
        }
        catch (Exception ex) { LastError = ex.Message; Connected = false; }
    }

    private async Task DiscoverToolsAsync(CancellationToken ct)
    {
        _tools.Clear();
        try
        {
            var result = await RpcAsync("tools/list", null, ct);
            if (result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in arr.EnumerateArray())
                {
                    var name   = t.TryGetProperty("name",        out var n) ? n.GetString() ?? "" : "";
                    var desc   = t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var schema = t.TryGetProperty("inputSchema", out var s)
                        ? s.Clone()
                        : JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""");
                    if (!string.IsNullOrEmpty(name))
                        _tools.Add(new McpTool(name, desc, schema));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mcp] tool discovery failed for '{_config.Id}': {ex.Message}");
        }
    }

    public async Task<string> CallToolAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        try
        {
            var result = await RpcAsync("tools/call", new { name = toolName, arguments = args }, ct);

            if (result.TryGetProperty("isError", out var isErr) && isErr.GetBoolean())
            {
                var errParts = ExtractTextContent(result);
                return $"Tool error: {string.Join("\n", errParts)}";
            }

            var parts = ExtractTextContent(result);
            return parts.Count > 0 ? string.Join("\n", parts) : result.GetRawText();
        }
        catch (Exception ex) { return $"MCP error: {ex.Message}"; }
    }

    private static List<string> ExtractTextContent(JsonElement result)
    {
        var parts = new List<string>();
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return parts;

        foreach (var item in content.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "text" && item.TryGetProperty("text", out var txt))
                parts.Add(txt.GetString() ?? "");
            else if (type == "resource" && item.TryGetProperty("resource", out var res))
            {
                if (res.TryGetProperty("text", out var rtxt))
                    parts.Add(rtxt.GetString() ?? "");
            }
        }
        return parts;
    }

    private Task<JsonElement> RpcAsync(string method, object? @params, CancellationToken ct) =>
        _config.Type == "http"
            ? RpcHttpAsync(method, @params, ct)
            : RpcStdioAsync(method, @params, ct);

    private async Task<JsonElement> RpcHttpAsync(string method, object? @params, CancellationToken ct)
    {
        var id   = Interlocked.Increment(ref _nextId);
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        var resp = await Http.PostAsync(_config.Url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("result", out var r)) return r.Clone();
        if (doc.RootElement.TryGetProperty("error",  out var e)) throw new Exception(e.GetRawText());
        return doc.RootElement.Clone();
    }

    private async Task<JsonElement> RpcStdioAsync(string method, object? @params, CancellationToken ct)
    {
        var id  = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        var line = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        await _writeLock.WaitAsync(ct);
        try   { await _stdin!.WriteLineAsync(line); await _stdin.FlushAsync(); }
        finally { _writeLock.Release(); }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        await using (timeoutCts.Token.Register(() =>
        {
            lock (_pending) _pending.Remove(id);
            tcs.TrySetCanceled();
        }))
        {
            return await tcs.Task;
        }
    }

    private async Task SendNotificationAsync(string method)
    {
        var line = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = (object?)null });
        await _writeLock.WaitAsync();
        try   { await _stdin!.WriteLineAsync(line); await _stdin.FlushAsync(); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stdout != null)
            {
                var line = await _stdout.ReadLineAsync(ct);
                if (line == null) break; // process exited

                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("id", out var idEl)) continue; // notification
                    var id = idEl.GetInt32();

                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_pending) { _pending.TryGetValue(id, out tcs); _pending.Remove(id); }
                    if (tcs == null) continue;

                    if (doc.RootElement.TryGetProperty("result", out var result))
                        tcs.TrySetResult(result.Clone());
                    else if (doc.RootElement.TryGetProperty("error", out var error))
                        tcs.TrySetException(new Exception(error.GetRawText()));
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mcp] read loop error '{_config.Id}': {ex.Message}");
            Connected = false;

            List<TaskCompletionSource<JsonElement>> pending;
            lock (_pending) { pending = [.. _pending.Values]; _pending.Clear(); }
            foreach (var tcs in pending)
                tcs.TrySetException(new Exception("MCP connection lost"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_readLoop != null) try { await _readLoop; } catch { }
        try { _stdin?.Dispose(); }    catch { }
        try { _process?.Kill(); _process?.Dispose(); } catch { }
    }
}
