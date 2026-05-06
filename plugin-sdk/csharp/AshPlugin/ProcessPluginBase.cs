using System.Text.Json;

namespace AshPlugin;

/// <summary>
/// Base class for Ash Server process plugins (stdin → stdout).
///
/// Usage:
///   class MyPlugin : ProcessPluginBase
///   {
///       protected override void RegisterTools(IToolRegistry reg)
///       {
///           reg.Register("echo", "Echoes a message",
///               async args => args.TryGetProperty("message", out var m)
///                   ? m.GetString() ?? ""
///                   : "");
///       }
///   }
///
///   // In Program.cs:
///   await new MyPlugin().RunAsync();
/// </summary>
public abstract class ProcessPluginBase
{
    protected abstract void RegisterTools(IToolRegistry registry);

    public async Task RunAsync()
    {
        var registry = new ToolRegistry();
        RegisterTools(registry);

        try
        {
            var raw = await Console.In.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                Console.Write("Empty input");
                return;
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Tool name can come from a "_tool" key if the server injects it,
            // otherwise defaults to the first registered tool.
            string toolName;
            if (root.TryGetProperty("_tool", out var tn))
                toolName = tn.GetString() ?? "";
            else
                toolName = registry.ToolNames.FirstOrDefault() ?? "";

            var result = await registry.Execute(toolName, root);
            Console.Write(result);
        }
        catch (Exception ex)
        {
            Console.Write($"Plugin error: {ex.Message}");
            Environment.Exit(1);
        }
    }
}
