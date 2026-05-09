using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AshServer.Chat.Slack;

public sealed class SlackBot : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SlackBot> _log;

    public SlackBot(IConfiguration config, ILogger<SlackBot> log)
    {
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue("ThirdPartyChat:Slack:Enabled", false);
        if (!enabled)
        {
            _log.LogInformation("[slack] Disabled — set ThirdPartyChat:Slack:Enabled=true to enable");
            return;
        }
        _log.LogWarning("[slack] Slack Socket Mode integration requires configuration — see docs");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
