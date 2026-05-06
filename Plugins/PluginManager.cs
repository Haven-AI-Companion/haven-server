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

        if (!Directory.Exists(_pluginsDir))
        {
            Console.WriteLine($"[plugins] directory not found: {_pluginsDir}");
            return;
        }

        foreach (var dir in Directory.GetDirectories(_pluginsDir).OrderBy(d => d))
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

    private async Task<string> ExecuteHttp(PluginTool tool, JsonElement args)
    {
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
            var psi = new ProcessStartInfo(tool.Handler.Command,
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
