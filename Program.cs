using Microsoft.AspNetCore.RateLimiting;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using AshServer.AI;
using AshServer.Auth;
using AshServer.Chat;
using AshServer.Data;
using AshServer.Mcp;
using AshServer.Personality;
using AshServer.Plugins;
using AshServer.Service;

namespace AshServer;

public class Program
{
    public static async Task Main(string[] args)
    {
        // ── Service management commands ─────────────────────────────────────
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--install-service":
                case "install-service":
                    ServiceInstaller.Install();
                    return;
                case "--uninstall-service":
                case "uninstall-service":
                    ServiceInstaller.Uninstall();
                    return;
                case "--service-status":
                case "service-status":
                    ServiceInstaller.Status();
                    return;
            }
        }

        var builder = WebApplication.CreateBuilder(args);

        // ── Native service hosting (auto-detects OS) ─────────────────────────
        builder.Host.UseWindowsService(opts => opts.ServiceName = "ash-server");
        builder.Host.UseSystemd();

        // ── Config ──────────────────────────────────────────────────────────
        // Merge appsettings.json with optional config.json beside the exe
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(configPath))
            builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);

        // Configure Kestrel based on network settings in configuration
        builder.WebHost.ConfigureKestrel(options =>
        {
            var port = builder.Configuration.GetValue("Port", 18799);
            var host = builder.Configuration.GetValue("Host", "0.0.0.0")?.Trim();

            if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "*" || host.Equals("any", StringComparison.OrdinalIgnoreCase))
            {
                options.ListenAnyIP(port);
            }
            else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1")
            {
                options.ListenLocalhost(port);
            }
            else
            {
                if (System.Net.IPAddress.TryParse(host, out var ip))
                {
                    options.Listen(ip, port);
                }
                else
                {
                    // Fallback to ListenAnyIP if host configuration is invalid or unparsed
                    Console.WriteLine($"[startup] Warning: Unrecognized Host config '{host}'. Falling back to ListenAnyIP.");
                    options.ListenAnyIP(port);
                }
            }
        });

        // Auto-generate a secure JWT secret on first run and persist it to config.json
        // so it survives restarts without requiring manual configuration.
        const string defaultSecretPlaceholder = "CHANGE_THIS_TO_A_RANDOM_SECRET_AT_LEAST_32_CHARS_LONG";
        var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "";
        if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret == defaultSecretPlaceholder || jwtSecret.Length < 32)
        {
            jwtSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
            // Persist to config.json so it is stable across restarts
            var genConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            System.Text.Json.Nodes.JsonObject cfgRoot;
            if (File.Exists(genConfigPath))
            {
                try { cfgRoot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(genConfigPath))!.AsObject(); }
                catch { cfgRoot = new System.Text.Json.Nodes.JsonObject(); }
            }
            else { cfgRoot = new System.Text.Json.Nodes.JsonObject(); }

            if (!cfgRoot.ContainsKey("Jwt")) cfgRoot["Jwt"] = new System.Text.Json.Nodes.JsonObject();
            cfgRoot["Jwt"]!.AsObject()["Secret"] = jwtSecret;
            File.WriteAllText(genConfigPath, cfgRoot.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            builder.Configuration["Jwt:Secret"] = jwtSecret; // sync in-memory config so AuthService uses same key
            Console.WriteLine("[startup] Generated new JWT secret and saved to config.json");
        }

        // ── Services ────────────────────────────────────────────────────────
        var dbPath = builder.Configuration["DatabasePath"] ?? "ash_server.db";
        var db = new Database(dbPath);
        db.Initialize();
        builder.Services.AddSingleton(db);

        var personalityDir = builder.Configuration["PersonalityDir"] ?? "personality";
        var personality = new PersonalityLoader(personalityDir);
        personality.Load();
        builder.Services.AddSingleton(personality);

        var backendManager = new BackendManager(db);
        builder.Services.AddSingleton(backendManager);

        builder.Services.AddSingleton<AshServer.Plugins.PluginManager>();
        builder.Services.AddSingleton<McpManager>();
        builder.Services.AddSingleton<UpdateManager>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<AshServer.Chat.IdentityResolver>();
        builder.Services.AddSingleton<AshServer.Chat.Discord.DiscordMessageRouter>();
        builder.Services.AddSingleton<AshServer.Middleware.ExternalRateLimiter>();
        builder.Services.AddSingleton<AshServer.Chat.PromptGuard>();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ChatHandler>();
        builder.Services.AddHostedService<AshServer.Chat.Discord.DiscordBot>();
        builder.Services.AddHostedService<AshServer.Chat.Telegram.TelegramBot>();
        // SlackBot registered as singleton so SlackEventsController can inject it
        builder.Services.AddSingleton<AshServer.Chat.Slack.SlackBot>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AshServer.Chat.Slack.SlackBot>());
        builder.Services.AddHttpClient();

        // ── HTTP API rate limiting ───────────────────────────────────────────
        builder.Services.AddRateLimiter(opts =>
        {
            opts.RejectionStatusCode = 429;
            opts.AddFixedWindowLimiter("api", policy =>
            {
                policy.PermitLimit = builder.Configuration.GetValue("RateLimit:Http:PermitLimit", 60);
                policy.Window = TimeSpan.FromSeconds(
                    builder.Configuration.GetValue("RateLimit:Http:WindowSeconds", 60));
                policy.QueueLimit = 0;
            });
        });

        builder.Services.AddControllers()
            .AddJsonOptions(opts =>
            {
                opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            });

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                };
                // Allow token from WebSocket query string
                opts.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["token"].ToString();
                        if (!string.IsNullOrEmpty(token)) ctx.Token = token;
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorization();

        // ── App ─────────────────────────────────────────────────────────────
        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapControllers();

        // Initialize MCP servers (non-fatal — server starts even if MCP servers fail)
        var mcpManager = app.Services.GetRequiredService<McpManager>();
        await mcpManager.InitializeAsync();

        // ── WebSocket endpoint ──────────────────────────────────────────────
        app.Map("/ws/{sessionId}", async (HttpContext ctx, string sessionId, ChatHandler chat, Database dbSvc) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

            var requireAuth = builder.Configuration.GetValue("RequireAuth", true);
            int userId = -1;
            string username = "local";

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();

            if (requireAuth)
            {
                // Read first message — it must be {"token":"..."}
                var buf = new byte[4096];
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();
                do
                {
                    result = await ws.ReceiveAsync(buf, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                string? jwtToken = null;
                try
                {
                    var doc = JsonDocument.Parse(ms.ToArray());
                    doc.RootElement.TryGetProperty("token", out var t);
                    jwtToken = t.GetString();
                }
                catch { }

                if (string.IsNullOrEmpty(jwtToken))
                {
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None);
                    return;
                }

                try
                {
                    var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                    var principal = tokenHandler.ValidateToken(jwtToken, new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                        ValidateIssuer = false,
                        ValidateAudience = false,
                    }, out _);
                    userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
                    username = principal.FindFirstValue(ClaimTypes.Name)!;
                }
                catch
                {
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None);
                    return;
                }
            }

            // Load user permissions for this session
            bool isAdmin = false;
            HashSet<string>? permissions = null;
            if (!requireAuth)
            {
                // No-auth mode: treat the local connection as having full access
                isAdmin = true;
                permissions = [.. AshServer.Auth.Permissions.All];
            }
            else if (userId > 0)
            {
                var user = await dbSvc.GetUserById(userId);
                isAdmin = user?.IsAdmin ?? false;
                permissions = isAdmin
                    ? [.. AshServer.Auth.Permissions.All]
                    : await dbSvc.GetUserPermissions(userId);
            }

            await chat.Handle(ctx, ws, userId, username, isAdmin, permissions);
        });

        // ── Fallback: serve chat.html for /chat and / ───────────────────────
        app.MapFallbackToFile("index.html");

        var port = builder.Configuration.GetValue("Port", 18799);
        var host = builder.Configuration.GetValue("Host", "0.0.0.0")?.Trim();
        Console.WriteLine($"""
            🌸 Ash Server (C#) starting on http://{host}:{port}
               Database: {dbPath}
               Personality: {personalityDir}
            """);

        app.Run();
    }
}

