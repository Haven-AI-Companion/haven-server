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
using AshServer.Personality;
using AshServer.Plugins;

namespace AshServer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ── Config ──────────────────────────────────────────────────────────
        // Merge appsettings.json with optional config.json beside the exe
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(configPath))
            builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);

        var jwtSecret = builder.Configuration["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret must be set in appsettings.json or config.json");

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
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<ChatHandler>();

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
        app.MapControllers();

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
            if (userId > 0)
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
        var url = $"http://0.0.0.0:{port}";
        Console.WriteLine($"""
            🌸 Ash Server (C#) starting on http://0.0.0.0:{port}
               Database: {dbPath}
               Personality: {personalityDir}
            """);

        app.Run(url);
    }
}

