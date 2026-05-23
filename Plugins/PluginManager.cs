using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AshServer.Plugins;

public class PluginManager
{
    private readonly string _pluginsDir;
    private readonly List<PluginManifest> _plugins = [];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<PluginManifest> Plugins => _plugins;

    public int LoadedCount  => _plugins.Count;
    public int EnabledCount => _plugins.Count(p => p.Enabled);

    public PluginManager(IConfiguration config, IWebHostEnvironment env)
    {
        _pluginsDir = config["PluginsDir"] ?? Path.Combine(env.ContentRootPath, "plugins");
        Reload();
    }

    public void Reload()
    {
        _plugins.Clear();

        // Pre-built zips ship "Plugins" (capital P); resolve the actual path with a
        // case-insensitive fallback so Linux case-sensitive filesystems work OOTB.
        var pluginsDir = _pluginsDir;
        if (!Directory.Exists(pluginsDir))
        {
            var parent = Path.GetDirectoryName(pluginsDir) ?? pluginsDir;
            var altCasing = Directory.EnumerateDirectories(parent)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "plugins",
                    StringComparison.OrdinalIgnoreCase));

            if (altCasing != null)
            {
                Console.WriteLine($"[plugins] using directory with alternate casing: {altCasing}");
                pluginsDir = altCasing;
            }
            else
            {
                Console.WriteLine($"[plugins] directory not found: {pluginsDir}");
                return;
            }
        }

        foreach (var dir in Directory.GetDirectories(pluginsDir).OrderBy(d => d))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(manifestPath), Opts);
                if (manifest != null)
                {
                    manifest.DirectoryPath = dir;
                    _plugins.Add(manifest);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[plugins] failed to load '{Path.GetFileName(dir)}': {ex.Message}");
            }
        }

        Console.WriteLine($"[plugins] loaded {_plugins.Count} plugin(s) ({EnabledCount} enabled)");
    }

    /// <summary>Returns all plugin tools merged into a JsonElement array (for ChatWithTools).</summary>
    public JsonElement GetToolDefinitions()
    {
        var tools = new List<object>();
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            foreach (var tool in plugin.Tools)
            {
                tools.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name        = tool.Name,
                        description = tool.Description,
                        parameters  = tool.Parameters.ValueKind != JsonValueKind.Undefined
                            ? tool.Parameters
                            : JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""")
                    }
                });
            }
        }
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(tools));
    }

    public bool IsPluginTool(string toolName) =>
        _plugins.Any(p => p.Enabled && p.Tools.Any(t => t.Name == toolName));

    public async Task<string> ExecuteTool(string toolName, JsonElement args)
    {
        var match = _plugins
            .Where(p => p.Enabled)
            .SelectMany(p => p.Tools.Select(t => (Plugin: p, Tool: t)))
            .FirstOrDefault(x => x.Tool.Name == toolName);

        if (match == default) return $"Plugin tool '{toolName}' not found";

        return match.Tool.Handler.Type switch
        {
            "http"    => await ExecuteHttp(match.Tool, args),
            "process" => await ExecuteProcess(match.Plugin, match.Tool, args),
            _         => $"Unknown handler type: '{match.Tool.Handler.Type}'"
        };
    }

    private static readonly HashSet<string> _blockedHosts = new(StringComparer.OrdinalIgnoreCase)
        { "169.254.169.254", "metadata.google.internal" };

    private static bool IsPrivateUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
        if (uri.Scheme != "http" && uri.Scheme != "https") return true;
        if (_blockedHosts.Contains(uri.Host)) return true;
        if (uri.HostNameType == UriHostNameType.IPv4)
        {
            var parts = uri.Host.Split('.');
            if (parts.Length == 4 && byte.TryParse(parts[0], out var a))
            {
                // Block 10.x, 172.16-31.x, 192.168.x, 127.x
                if (a == 10 || a == 127) return true;
                if (a == 172 && byte.TryParse(parts[1], out var b) && b >= 16 && b <= 31) return true;
                if (a == 192 && parts[1] == "168") return true;
            }
        }
        if (uri.IsLoopback) return true;
        return false;
    }

    private async Task<string> ExecuteHttp(PluginTool tool, JsonElement args)
    {
        if (IsPrivateUrl(tool.Handler.Url))
            return $"HTTP plugin tool '{tool.Name}' rejected: URL targets a private/loopback address.";
        try
        {
            var body = JsonSerializer.Serialize(new { tool = tool.Name, args });
            var resp = await Http.PostAsync(tool.Handler.Url,
                new StringContent(body, Encoding.UTF8, "application/json"));
            return await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { return $"HTTP tool error: {ex.Message}"; }
    }

    private static async Task<string> ExecuteProcess(PluginManifest plugin, PluginTool tool, JsonElement args)
    {
        try
        {
            // Security: command must be a relative path and must resolve inside the plugin directory.
            var command = tool.Handler.Command;
            if (Path.IsPathRooted(command))
                return $"Plugin tool '{tool.Name}' rejected: command must be a relative path, not absolute.";

            var resolvedCommand = Path.GetFullPath(Path.Combine(plugin.DirectoryPath, command));
            if (!resolvedCommand.StartsWith(Path.GetFullPath(plugin.DirectoryPath) + Path.DirectorySeparatorChar) &&
                resolvedCommand != Path.GetFullPath(plugin.DirectoryPath))
                return $"Plugin tool '{tool.Name}' rejected: command path escapes plugin directory.";

            if (!File.Exists(resolvedCommand))
                return $"Plugin tool '{tool.Name}' error: executable not found at '{resolvedCommand}'.";

            var psi = new ProcessStartInfo(resolvedCommand,
                string.Join(" ", tool.Handler.Args))
            {
                WorkingDirectory       = plugin.DirectoryPath,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi) ?? throw new Exception("Failed to start process");
            await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(args));
            proc.StandardInput.Close();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            return string.IsNullOrWhiteSpace(output) ? "Process returned no output" : output.Trim();
        }
        catch (Exception ex) { return $"Process tool error: {ex.Message}"; }
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        var plugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin == null) return;
        if (plugin.Builtin) return; // built-in plugins cannot be toggled externally

        plugin.Enabled = enabled;

        // Persist to manifest
        var manifestPath = Path.Combine(plugin.DirectoryPath, "plugin.json");
        try
        {
            var raw = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(raw);
            var dict = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            dict["enabled"] = enabled;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(dict,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugins] failed to persist enabled state for '{pluginId}': {ex.Message}");
        }
    }
}
