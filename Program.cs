using Microsoft.AspNetCore.RateLimiting;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using AshServer.AI;
using AshServer.Models;
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
        builder.Host.UseWindowsService(opts => opts.ServiceName = "haven-server");
        builder.Host.UseSystemd();

        // ── Config ──────────────────────────────────────────────────────────
        // Merge appsettings.json with optional config.json beside the exe
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(configPath))
            builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);

        // Configure Kestrel based on network settings in configuration
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Configure aggressive TCP Keep-Alives (10s time, 3s interval, 3 retries) on all incoming sockets
            // to instantly tear down dead connections from app force-closes, NAT timeouts, or cellular drops
            options.ConfigureEndpointDefaults(listenOptions =>
            {
                listenOptions.Use(next => async connectionContext =>
                {
                    var socketFeature = connectionContext.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionSocketFeature>();
                    if (socketFeature?.Socket != null)
                    {
                        var socket = socketFeature.Socket;
                        try
                        {
                            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);
                            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 10);
                            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 3);
                            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount, 3);
                        }
                        catch {}
                    }
                    await next(connectionContext);
                });
            });

            var port = builder.Configuration.GetValue("Port", 18799);
            
            var bindInterface = builder.Configuration["BindInterface"]?.Trim();
            if (!string.IsNullOrEmpty(bindInterface))
            {
                var targetIp = DiscoverInterfaceIp(bindInterface);
                if (targetIp != null)
                {
                    Console.WriteLine($"[startup] BindInterface '{bindInterface}' is configured. Binding Kestrel to: {targetIp}:{port}");
                    options.Listen(targetIp, port);
                    return;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[startup] ERROR: Configured BindInterface '{bindInterface}' not found or has no active IPv4! Falling back to localhost (127.0.0.1).");
                    Console.ResetColor();
                    options.ListenLocalhost(port);
                    return;
                }
            }

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
            
            // Sync to project root config.json to survive clean builds during development (skip if testing)
            var checkDbPath = builder.Configuration["DatabasePath"] ?? "haven_server.db";
            if (!checkDbPath.Contains("_test", StringComparison.OrdinalIgnoreCase))
            {
                var currentDir = AppContext.BaseDirectory;
                while (!string.IsNullOrEmpty(currentDir))
                {
                    try
                    {
                        var csprojFiles = Directory.GetFiles(currentDir, "*.csproj");
                        if (csprojFiles.Length > 0)
                        {
                            var rootConfig = Path.Combine(currentDir, "config.json");
                            if (File.Exists(rootConfig) && Path.GetFullPath(rootConfig) != Path.GetFullPath(genConfigPath))
                            {
                                var rootText = File.ReadAllText(rootConfig);
                                var rootNode = System.Text.Json.Nodes.JsonNode.Parse(rootText)!.AsObject();
                                if (!rootNode.ContainsKey("Jwt")) rootNode["Jwt"] = new System.Text.Json.Nodes.JsonObject();
                                rootNode["Jwt"]!.AsObject()["Secret"] = jwtSecret;
                                File.WriteAllText(rootConfig, rootNode.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                                Console.WriteLine($"[startup] Synced JWT secret back to root config.json to survive rebuilds.");
                            }
                            break;
                        }
                    }
                    catch {}
                    currentDir = Path.GetDirectoryName(currentDir);
                }
            }

            builder.Configuration["Jwt:Secret"] = jwtSecret; // sync in-memory config so AuthService uses same key
            Console.WriteLine("[startup] Generated new JWT secret and saved to config.json");
        }

        // ── Services ────────────────────────────────────────────────────────
        var dbPath = builder.Configuration["DatabasePath"] ?? "haven_server.db";
        if (dbPath == "haven_server.db")
        {
            var oldDb = "ash_server.db";
            if (!File.Exists(dbPath) && File.Exists(oldDb))
            {
                try
                {
                    File.Copy(oldDb, dbPath);
                    Console.WriteLine("[startup] Migrated database from 'ash_server.db' to 'haven_server.db'.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[startup] Failed to migrate database: {ex.Message}");
                    dbPath = oldDb; // Fallback
                }
            }
        }
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

        var personalityDir = builder.Configuration["PersonalityDir"] ?? builder.Configuration["personality:path"] ?? "personality";
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
        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        provider.Mappings[".glb"] = "model/gltf-binary";
        provider.Mappings[".vrm"] = "application/octet-stream";
        app.UseStaticFiles(new StaticFileOptions
        {
            ContentTypeProvider = provider
        });
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

        // Register host lifetime events to ensure background sidecar processes (llama-server and sd-server) are killed cleanly on exit
        var lifetime = app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            Console.WriteLine("[lifetime] Application stopping. Initiating cleanup of local backend processes...");
            try
            {
                profiler.StopLocalBackend();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lifetime] Error during backend process cleanup: {ex.Message}");
            }
        });

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

        // ── Voice Call WebSocket endpoint ────────────────────────────────────
        // Protocol: 1) Client sends configuration handshake {"token":"...", "systemPrompt":"...", "voiceId":"..."}
        //           2) Client sends binary WAV audio chunks
        //           3) Server responds with transcription {"type":"transcription","text":"..."}
        //              then companion text reply {"type":"speech_text","text":"..."}
        //              then binary WAV audio bytes for TTS
        app.Map("/ws/voice/{characterId}", async (HttpContext ctx, string characterId, BackendManager backends, IConfiguration config) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

            var requireAuth = builder.Configuration.GetValue("RequireAuth", true);
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();

            // ── Handshake (First message) ──
            var firstMsgBuf = new byte[8192];
            using var firstMsgMs = new MemoryStream();
            WebSocketReceiveResult firstMsgResult;
            do
            {
                firstMsgResult = await ws.ReceiveAsync(firstMsgBuf, CancellationToken.None);
                if (firstMsgResult.MessageType == WebSocketMessageType.Close) return;
                firstMsgMs.Write(firstMsgBuf, 0, firstMsgResult.Count);
            } while (!firstMsgResult.EndOfMessage);

            string? jwtToken = null;
            string systemPrompt = "You are a helpful assistant.";
            string voiceId = "en_US-amy-medium";
            string characterName = "Companion";

            try
            {
                using var doc = JsonDocument.Parse(firstMsgMs.ToArray());
                if (doc.RootElement.TryGetProperty("token", out var t)) jwtToken = t.GetString();
                if (doc.RootElement.TryGetProperty("systemPrompt", out var sp)) systemPrompt = sp.GetString() ?? systemPrompt;
                if (doc.RootElement.TryGetProperty("voiceId", out var vi)) voiceId = vi.GetString() ?? voiceId;
                if (doc.RootElement.TryGetProperty("characterName", out var cn)) characterName = cn.GetString() ?? characterName;
            }
            catch { }

            // ── Auth Validation ──
            if (requireAuth)
            {
                if (string.IsNullOrEmpty(jwtToken))
                {
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None);
                    return;
                }
                try
                {
                    var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                    tokenHandler.ValidateToken(jwtToken, new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                        ValidateIssuer = false,
                        ValidateAudience = false,
                    }, out _);
                }
                catch
                {
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None);
                    return;
                }
            }

            // ── Setup paths ──
            var whisperExe = @"C:\Users\admin\whisper-cpp\Release\whisper-cli.exe";
            var whisperModel = @"C:\Users\admin\whisper-cpp\models\ggml-base.en.bin";
            var piperExe = @"C:\Users\admin\piper\piper\piper.exe";
            var piperModel = $@"C:\Users\admin\piper\piper\models\{voiceId}.onnx";
            var piperConfig = $@"C:\Users\admin\piper\piper\models\{voiceId}.onnx.json";

            if (!File.Exists(piperModel))
            {
                piperModel = @"C:\Users\admin\piper\piper\models\en_US-amy-medium.onnx";
                piperConfig = @"C:\Users\admin\piper\piper\models\en_US-amy-medium.onnx.json";
            }

            var modelId = config["DefaultModel"] ?? "default";
            var history = new List<ChatMessage> { new ChatMessage("system", systemPrompt) };
            string? lastCameraFrameBase64 = null;

            // ── Main Receive & Process Loop ──
            while (ws.State == WebSocketState.Open)
            {
                using var audioMs = new MemoryStream();
                using var textMs = new MemoryStream();
                var recvBuf = new byte[8192];
                WebSocketReceiveResult recvResult;
                bool isText = false;

                do
                {
                    recvResult = await ws.ReceiveAsync(recvBuf, CancellationToken.None);
                    if (recvResult.MessageType == WebSocketMessageType.Close) return;
                    if (recvResult.MessageType == WebSocketMessageType.Binary)
                    {
                        audioMs.Write(recvBuf, 0, recvResult.Count);
                    }
                    else if (recvResult.MessageType == WebSocketMessageType.Text)
                    {
                        isText = true;
                        textMs.Write(recvBuf, 0, recvResult.Count);
                    }
                } while (!recvResult.EndOfMessage);

                if (isText)
                {
                    try
                    {
                        var textStr = Encoding.UTF8.GetString(textMs.ToArray());
                        using var doc = JsonDocument.Parse(textStr);
                        if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "camera_frame")
                        {
                            if (doc.RootElement.TryGetProperty("image", out var imgProp))
                            {
                                lastCameraFrameBase64 = imgProp.GetString();
                                var ackMsg = JsonSerializer.Serialize(new { type = "camera_ack", status = "ok" });
                                await ws.SendAsync(Encoding.UTF8.GetBytes(ackMsg), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                        }
                    }
                    catch { }
                    continue;
                }

                if (audioMs.Length == 0) continue;

                var tmpWav = Path.Combine(Path.GetTempPath(), $"voice_{Guid.NewGuid():N}.wav");
                await File.WriteAllBytesAsync(tmpWav, audioMs.ToArray());

                try
                {
                    // ── 1. Transcription ──
                    string transcription = "";
                    if (File.Exists(whisperExe) && File.Exists(whisperModel))
                    {
                        var whisperProc = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = whisperExe,
                                Arguments = $"-m \"{whisperModel}\" -f \"{tmpWav}\" --output-txt --no-timestamps -l en",
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }
                        };
                        whisperProc.Start();
                        var whisperOut = await whisperProc.StandardOutput.ReadToEndAsync();
                        await whisperProc.WaitForExitAsync();
                        transcription = whisperOut.Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "";
                        transcription = System.Text.RegularExpressions.Regex.Replace(transcription, @"\[.*?\]", "").Trim();
                    }
                    else
                    {
                        transcription = "[Whisper not available]";
                    }

                    // Stream transcription to client
                    var transcriptionMsg = JsonSerializer.Serialize(new { type = "transcription", text = transcription });
                    await ws.SendAsync(
                        Encoding.UTF8.GetBytes(transcriptionMsg),
                        WebSocketMessageType.Text, true, CancellationToken.None);

                    if (string.IsNullOrWhiteSpace(transcription)) continue;

                    var imgList = lastCameraFrameBase64 != null ? new List<string> { lastCameraFrameBase64 } : null;
                    history.Add(new ChatMessage("user", transcription, imgList));
                    lastCameraFrameBase64 = null;
                    if (history.Count > 40)
                    {
                        history.RemoveRange(1, 2);
                    }

                    // ── 2. LLM response ──
                    var replyBuilder = new StringBuilder();
                    await foreach (var token in backends.StreamChat(modelId, history))
                    {
                        replyBuilder.Append(token);
                    }

                    var replyText = replyBuilder.ToString().Trim();
                    replyText = System.Text.RegularExpressions.Regex.Replace(replyText, @"<thought>[\s\S]*?</thought>", "").Trim();

                    history.Add(new ChatMessage("assistant", replyText));

                    // Stream companion text reply to client
                    var speechMsg = JsonSerializer.Serialize(new { type = "speech_text", text = replyText });
                    await ws.SendAsync(
                        Encoding.UTF8.GetBytes(speechMsg),
                        WebSocketMessageType.Text, true, CancellationToken.None);

                    // ── 3. TTS synthesis ──
                    if (File.Exists(piperExe) && File.Exists(piperModel) && replyText.Length > 0)
                    {
                        var ttsOut = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.wav");
                        var escaped = replyText.Replace("\"", "\\\"").Replace("\n", " ");
                        var piperProc = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/c echo {escaped} | \"{piperExe}\" -m \"{piperModel}\" -c \"{piperConfig}\" -f \"{ttsOut}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }
                        };
                        piperProc.Start();
                        await piperProc.WaitForExitAsync();

                        if (File.Exists(ttsOut))
                        {
                            var audioBytes = await File.ReadAllBytesAsync(ttsOut);
                            await ws.SendAsync(audioBytes, WebSocketMessageType.Binary, true, CancellationToken.None);
                            File.Delete(ttsOut);
                        }
                    }
                }
                finally
                {
                    if (File.Exists(tmpWav)) File.Delete(tmpWav);
                }
            }
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
            🌸 Haven Server (C#) starting on http://{host}:{port}
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

    public static System.Net.IPAddress? DiscoverInterfaceIp(string interfaceName)
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                
                if (ni.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase) || 
                    ni.Description.Contains(interfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    var ipProps = ni.GetIPProperties();
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
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
