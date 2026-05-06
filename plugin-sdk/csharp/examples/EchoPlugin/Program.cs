using System.Text.Json;
using AshPlugin;

// Echo Plugin — example C# HTTP plugin

var plugin = new EchoPlugin();
plugin.Run(args, port: 19000);

class EchoPlugin : HttpPluginBase
{
    protected override void RegisterTools(IToolRegistry reg)
    {
        reg.Register("echo", "Echoes a message back exactly as given.",
            async args =>
            {
                await Task.CompletedTask;
                return args.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            });

        reg.Register("reverse", "Reverses the characters in a string.",
            async args =>
            {
                await Task.CompletedTask;
                var text = args.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                return new string(text.Reverse().ToArray());
            });
    }
}
