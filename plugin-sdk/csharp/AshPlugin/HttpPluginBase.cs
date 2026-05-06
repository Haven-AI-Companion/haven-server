using System.Text.Json;

namespace AshPlugin;

/// <summary>
/// Base class for Ash Server HTTP plugins (C# / ASP.NET minimal API).
///
/// Usage:
///   var plugin = new MyPlugin();
///   plugin.Run(args, port: 19000);
///
/// Example:
///   class MyPlugin : HttpPluginBase
///   {
///       protected override void RegisterTools(IToolRegistry reg)
///       {
///           reg.Register("echo", "Echoes a message", async args =>
///               args.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
///       }
///   }
/// </summary>
public abstract class HttpPluginBase
{
    protected abstract void RegisterTools(IToolRegistry registry);

    public void Run(string[] args, int port = 19000)
    {
        var registry = new ToolRegistry();
        RegisterTools(registry);

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapPost("/", async (HttpContext ctx) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                var toolName = root.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
                var toolArgs = root.TryGetProperty("args", out var a) ? a : default;

                var result = await registry.Execute(toolName, toolArgs);
                return Results.Text(result);
            }
            catch (Exception ex)
            {
                return Results.Text($"Plugin error: {ex.Message}", statusCode: 500);
            }
        });

        Console.WriteLine($"[ash-plugin] {GetType().Name} listening on http://0.0.0.0:{port}");
        Console.WriteLine($"[ash-plugin] Tools: {string.Join(", ", registry.ToolNames)}");
        app.Run();
    }
}

public interface IToolRegistry
{
    void Register(string name, string description, Func<JsonElement, Task<string>> handler);
}

internal class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, Func<JsonElement, Task<string>>> _handlers = [];

    public IEnumerable<string> ToolNames => _handlers.Keys;

    public void Register(string name, string description, Func<JsonElement, Task<string>> handler)
        => _handlers[name] = handler;

    public async Task<string> Execute(string toolName, JsonElement args)
    {
        if (!_handlers.TryGetValue(toolName, out var fn))
            return $"Unknown tool: {toolName}";
        return await fn(args);
    }
}
