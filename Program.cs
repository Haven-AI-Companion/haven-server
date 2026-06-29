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
        // Suppress default URL bindings from launchSettings/environment variables to prevent Kestrel override warnings at startup
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);

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

        // Bootstrap appsettings.json from appsettings.json.example if missing
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(appSettingsPath))
        {
            var examplePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json.example");
            if (File.Exists(examplePath))
            {
                try
                {
                    File.Copy(examplePath, appSettingsPath);
                    Console.WriteLine("[startup] Bootstrapped appsettings.json from example template.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[startup] Warning: Failed to copy appsettings.json: {ex.Message}");
                }
            }
        }

        var builder = WebApplication.CreateBuilder(args);

        // Map custom CLI flags to configuration keys
        if (args.Contains("--worker", StringComparer.OrdinalIgnoreCase))
        {
            builder.Configuration["Mode"] = "worker";
        }
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--master", StringComparison.OrdinalIgnoreCase))
            {
                builder.Configuration["Grid:MasterUrl"] = args[i + 1];
            }
            else if (args[i].Equals("--token", StringComparison.OrdinalIgnoreCase))
            {
                builder.Configuration["Grid:PairingToken"] = args[i + 1];
            }
        }

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
            var tailscaleOnly = builder.Configuration.GetValue("TailscaleOnly", false);

            if (tailscaleOnly)
            {
                var tsIp = DiscoverTailscaleIp();
                if (tsIp != null)
                {
                    Console.WriteLine($"[startup] TailscaleOnly is enabled. Binding Kestrel to Tailscale IP: {tsIp}:{port}");
                    options.Listen(tsIp, port);
                    return;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[startup] ERROR: TailscaleOnly is enabled, but no Tailscale interface was found! Falling back to localhost (127.0.0.1) for safety.");
                    Console.ResetColor();
                    options.ListenLocalhost(port);
                    return;
                }
            }

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

        // ── CLI Command: create-admin ───────────────────────────────────────
        if (args.Length > 0 && (args[0].Equals("create-admin", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--create-admin", StringComparison.OrdinalIgnoreCase)))
        {
            var auth = new AuthService(db, builder.Configuration);
            await CreateAdminCli(args, db, auth);
            return;
        }

        var personalityDir = builder.Configuration["PersonalityDir"] ?? "personality";
        var personality = new PersonalityLoader(personalityDir);
        personality.Load();
        builder.Services.AddSingleton(personality);
        builder.Services.AddSingleton<BackendManager>();
        builder.Services.AddSingleton<HardwareProfiler>();

        builder.Services.AddSingleton<AshServer.Plugins.PluginManager>();
        builder.Services.AddSingleton<McpManager>();
        builder.Services.AddSingleton<UpdateManager>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<RagService>();
        builder.Services.AddSingleton<GridManager>();
        builder.Services.AddHostedService<GridWorkerService>();
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

        // Initialize local backend (runs hardware profiling and starts llama-server if models exist)
        var profiler = app.Services.GetRequiredService<HardwareProfiler>();
        await profiler.InitializeLocalBackendAsync();

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

        // ── Grid Worker WebSocket endpoint ──────────────────────────────────
        app.Map("/api/grid/ws", async (HttpContext ctx, GridManager grid) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var webSocket = await ctx.WebSockets.AcceptWebSocketAsync();
            await grid.HandleWorkerConnectionAsync(webSocket, ctx);
        });

        // ── Fallback: serve chat.html for /chat and / ───────────────────────
        app.MapFallbackToFile("index.html");

        var port = builder.Configuration.GetValue("Port", 18799);
        var host = builder.Configuration.GetValue("Host", "0.0.0.0")?.Trim() ?? "0.0.0.0";

        PublicExposureDetected = IsPublicIpExposureDetected(host);
        if (PublicExposureDetected)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("""

                ⚠️  SECURITY WARNING: Public Network Exposure Detected!
                   Your Ash Server is accessible over the public internet on a non-private IP.
                   We highly recommend setting up a secure mesh VPN (Tailscale) and binding
                   the Host to '127.0.0.1' or your private VPN IP in config.json.

                """);
            Console.ResetColor();
        }

        Console.WriteLine($"""
            🌸 Ash Server (C#) starting on http://{host}:{port}
               Database: {dbPath}
               Personality: {personalityDir}
            """);

        app.Run();
    }

    public static bool PublicExposureDetected { get; private set; }

    public static bool IsPublicIpExposureDetected(string host)
    {
        // If explicitly binding to loopback only, it is never exposed publicly
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1")
        {
            return false;
        }

        // If explicitly binding to a private IP (not wildcard), it is not exposed publicly
        if (System.Net.IPAddress.TryParse(host, out var bindIp))
        {
            if (IsPrivateIp(bindIp)) return false;
        }

        // If wildcard (0.0.0.0 / [::]) or a public IP, scan interfaces for any public IP address
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                
                // Skip loopbacks
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    var ip = addr.Address;
                    
                    // Skip IPv6 Link-Local (fe80::) or loopbacks
                    if (ip.IsIPv6LinkLocal || System.Net.IPAddress.IsLoopback(ip)) continue;

                    // If we find any active IP that is NOT private, we have public exposure!
                    if (!IsPrivateIp(ip))
                    {
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static bool IsPrivateIp(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            int first = bytes[0];
            int second = bytes[1];

            // 10.0.0.0/8
            if (first == 10) return true;
            
            // 172.16.0.0/12
            if (first == 172 && second >= 16 && second <= 31) return true;
            
            // 192.168.0.0/16
            if (first == 192 && second == 168) return true;
            
            // 169.254.0.0/16 (Link-Local)
            if (first == 169 && second == 254) return true;

            // 100.64.0.0/10 (CGNAT / Tailscale)
            if (first == 100 && second >= 64 && second <= 127) return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        }

        return false;
    }

    private static async Task CreateAdminCli(string[] args, Database db, AuthService auth)
    {
        Console.WriteLine("\n🌸 Ash Server — Secure CLI Administrator Bootstrap\n");

        string? username = null;
        string? password = null;
        string? email = null;

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--username", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-u", StringComparison.OrdinalIgnoreCase))
            {
                username = args[i + 1];
            }
            else if (args[i].Equals("--password", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                password = args[i + 1];
            }
            else if (args[i].Equals("--email", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-e", StringComparison.OrdinalIgnoreCase))
            {
                email = args[i + 1];
            }
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            Console.Write("Enter Administrator Username: ");
            username = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Write("Enter Administrator Email: ");
            email = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            password = ReadPasswordSecurely();
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("[error] Username and Password cannot be empty.");
            Environment.Exit(1);
        }

        try
        {
            var existing = await db.GetUserByUsername(username);
            if (existing != null)
            {
                Console.WriteLine($"[error] A user with username '{username}' already exists.");
                Environment.Exit(1);
            }

            var passwordHash = auth.HashPassword(password);
            var user = await db.CreateUser(username, passwordHash, email, isAdmin: true);
            await db.ToggleAdmin(user.Id, true);

            Console.WriteLine($"\n[success] Administrator account '{username}' successfully created and bootstrapped!");
            Console.WriteLine("You can now securely log in to the web interface.\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[error] Failed to create admin account: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static string ReadPasswordSecurely()
    {
        var pass = new StringBuilder();
        ConsoleKeyInfo key;
        Console.Write("Enter Password: ");

        do
        {
            key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.Backspace)
            {
                if (pass.Length > 0)
                {
                    pass.Remove(pass.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (key.Key != ConsoleKey.Enter)
            {
                pass.Append(key.KeyChar);
                Console.Write("*");
            }
        } while (key.Key != ConsoleKey.Enter);

        Console.WriteLine();
        return pass.ToString();
    }

    public static System.Net.IPAddress? DiscoverTailscaleIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var ipBytes = addr.Address.GetAddressBytes();
                        // Tailscale IPs are in the CGNAT 100.64.0.0/10 range:
                        // 100.64.0.0 to 100.127.255.255
                        if (ipBytes[0] == 100 && ipBytes[1] >= 64 && ipBytes[1] <= 127)
                        {
                            return addr.Address;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }
}
