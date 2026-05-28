using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AshServer.AI;
using AshServer.Auth;
using AshServer.Data;
using AshServer.Mcp;
using AshServer.Models;
using AshServer.Service;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

namespace AshServer.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly Database _db;
    private readonly IConfiguration _config;

    public AuthController(AuthService auth, Database db, IConfiguration config)
    {
        _auth = auth;
        _db = db;
        _config = config;
    }

    [HttpGet("config")]
    public IActionResult GetConfig() => Ok(new
    {
        require_auth = _config.GetValue("RequireAuth", true),
        allow_registration = _config.GetValue("AllowRegistration", true)
    });

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromForm] RegisterRequest req)
    {
        if (!_config.GetValue("AllowRegistration", true))
            return BadRequest(new { error = "Registration is disabled" });

        var (user, error) = await _auth.Register(req.Username, req.Password, req.Email);
        if (error != null) return BadRequest(new { error });

        var token = _auth.GenerateToken(user!);
        return Ok(new LoginResponse(token, await _auth.ToInfoWithPerms(user!)));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromForm] LoginRequest req)
    {
        var (user, error) = await _auth.Login(req.Username, req.Password);
        if (error != null) return Unauthorized(new { error });

        var token = _auth.GenerateToken(user!);
        return Ok(new LoginResponse(token, await _auth.ToInfoWithPerms(user!)));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.GetUserById(userId);
        if (user == null) return Unauthorized();
        return Ok(new { user = await _auth.ToInfoWithPerms(user) });
    }

    [HttpGet("me/permissions")]
    [Authorize]
    public async Task<IActionResult> MyPermissions()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.GetUserById(userId);
        if (user == null) return Unauthorized();
        var perms = user.IsAdmin
            ? AshServer.Auth.Permissions.All.ToList()
            : [.. (await _db.GetUserPermissions(userId))];
        var roles = await _db.GetUserRoleNames(userId);
        return Ok(new { permissions = perms, roles, is_admin = user.IsAdmin });
    }

    [HttpPatch("me/email")]
    [Authorize]
    public async Task<IActionResult> UpdateEmail([FromBody] ChangeEmailRequest req)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _db.UpdateUserEmail(userId, req.Email);
        return Ok(new { ok = true });
    }

    [HttpPatch("me/password")]
    [Authorize]
    public async Task<IActionResult> UpdatePassword([FromBody] ChangePasswordRequest req)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.GetUserById(userId);
        if (user == null) return Unauthorized();
        if (!_auth.VerifyPassword(req.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect" });
        var hash = _auth.HashPassword(req.NewPassword);
        await _db.UpdateUserPassword(userId, hash);
        return Ok(new { ok = true });
    }
}

[ApiController]
[Route("api/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly Database _db;

    public ConversationsController(Database db) { _db = db; }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(await _db.GetConversations(UserId));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Dictionary<string, string>? body)
    {
        var title = body?.GetValueOrDefault("title") ?? "New Conversation";
        var id = await _db.CreateConversation(UserId, title);
        return Ok(new { id, title });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var conv = await _db.GetConversation(id, UserId);
        return conv == null ? NotFound() : Ok(conv);
    }

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> GetMessages(string id)
    {
        var conv = await _db.GetConversation(id, UserId);
        if (conv == null) return NotFound();
        return Ok(await _db.GetMessages(id));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        await _db.DeleteConversation(id, UserId);
        return Ok(new { ok = true });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Rename(string id, [FromBody] Dictionary<string, string> body)
    {
        var title = body.GetValueOrDefault("title") ?? "";
        if (string.IsNullOrEmpty(title)) return BadRequest(new { error = "title required" });
        await _db.RenameConversation(id, UserId, title);
        return Ok(new { ok = true });
    }

    [HttpGet("{id}/export")]
    public async Task<IActionResult> Export(string id, [FromQuery] string format = "json")
    {
        var conv = await _db.GetConversation(id, UserId);
        if (conv == null) return NotFound();
        var messages = await _db.GetMessages(id);
        var safeTitle = string.Join("_", (conv.Title ?? "conversation").Split(System.IO.Path.GetInvalidFileNameChars()));

        return format.ToLower() switch
        {
            "md" => File(System.Text.Encoding.UTF8.GetBytes(ToMarkdown(conv, messages)), "text/markdown", $"{safeTitle}.md"),
            "txt" => File(System.Text.Encoding.UTF8.GetBytes(ToPlainText(conv, messages)), "text/plain", $"{safeTitle}.txt"),
            _ => File(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
                    new { conversation = conv, messages },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true })),
                "application/json", $"{safeTitle}.json")
        };
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest(new { error = "q required" });
        var results = await _db.SearchConversations(UserId, q, Math.Min(limit, 100));
        return Ok(results.Select(r => new
        {
            conversation = r.Conv,
            match = new { r.Msg.Role, r.Msg.Content, r.Msg.CreatedAt }
        }));
    }

    private static string ToMarkdown(Conversation conv, List<Message> messages)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {conv.Title}");
        sb.AppendLine($"> Exported from Ash Server — {conv.CreatedAt}");
        sb.AppendLine();
        foreach (var m in messages)
        {
            sb.AppendLine(m.Role == "user" ? "**You:**" : "**Ash:**");
            sb.AppendLine();
            sb.AppendLine(m.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ToPlainText(Conversation conv, List<Message> messages)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(conv.Title);
        sb.AppendLine(new string('-', (conv.Title ?? "").Length));
        sb.AppendLine();
        foreach (var m in messages)
        {
            sb.AppendLine($"{(m.Role == "user" ? "You" : "Ash")}: {m.Content}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

[ApiController]
[Route("api")]
public class ModelsController : ControllerBase
{
    private readonly AshServer.AI.BackendManager _backends;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly Database _db;
    private readonly AshServer.Plugins.PluginManager _plugins;

    public ModelsController(AshServer.AI.BackendManager backends, IConfiguration config, IWebHostEnvironment env, Database db, AshServer.Plugins.PluginManager plugins)
    {
        _backends = backends;
        _config = config;
        _env = env;
        _db = db;
        _plugins = plugins;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAdmin => User.FindFirstValue("is_admin") == "true";

    [HttpGet("models")]
    public async Task<IActionResult> ListModels() =>
        Ok(await _backends.ListAllModels());

    [HttpGet("config")]
    public IActionResult GetConfig() => Ok(new
    {
        default_model = _config["DefaultModel"] ?? ""
    });

    [HttpGet("plugins")]
    [Authorize]
    public IActionResult ListPlugins() => Ok(new
    {
        plugins = _plugins.Plugins
            .Where(p => p.Enabled)
            .Select(p => new { p.Id, p.Name, p.Description, tool_count = p.Tools.Count })
    });

    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50 MB
    public async Task<IActionResult> Upload(IFormFile file)
    {
        // Permission gate — admins always bypass
        if (!IsAdmin && !await _db.UserHasPermission(UserId, AshServer.Auth.Permissions.FileUpload))
            return StatusCode(403, new { error = "You do not have permission to upload files." });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var safeName = $"{Guid.NewGuid()}{ext}";
        var fullPath = Path.Combine(uploadsDir, safeName);

        using (var stream = System.IO.File.Create(fullPath))
            await file.CopyToAsync(stream);

        var isImage = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp";
        var url = $"/uploads/{safeName}";

        string? base64 = null;
        string? textContent = null;

        if (isImage)
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(fullPath);
            base64 = Convert.ToBase64String(bytes);
        }
        else if (IsTextFile(ext))
        {
            const int maxTextBytes = 100 * 1024;
            using var fs = System.IO.File.OpenRead(fullPath);
            var buffer = new byte[Math.Min(maxTextBytes, (int)fs.Length)];
            var read = await fs.ReadAsync(buffer);
            textContent = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            if (fs.Length > maxTextBytes)
                textContent += $"\n\n[... file truncated at 100 KB, {fs.Length:N0} bytes total ...]";
        }

        return Ok(new
        {
            url,
            filename = file.FileName,
            size = file.Length,
            is_image = isImage,
            base64,
            text_content = textContent
        });
    }

    private static bool IsTextFile(string ext) =>
        ext is ".txt" or ".md" or ".csv" or ".json" or ".yaml" or ".yml"
            or ".py" or ".js" or ".ts" or ".jsx" or ".tsx"
            or ".cs" or ".java" or ".go" or ".rs" or ".cpp" or ".c" or ".h"
            or ".html" or ".css" or ".scss" or ".xml" or ".toml" or ".ini"
            or ".sh" or ".bat" or ".ps1" or ".sql" or ".log" or ".env";
}


[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly Database _db;
    private readonly AshServer.AI.BackendManager _backends;
    private readonly IConfiguration _config;
    private readonly AshServer.Personality.PersonalityLoader _personality;
    private readonly AshServer.Plugins.PluginManager _plugins;
    private readonly ILogger<AdminController> _log;
    private readonly IWebHostEnvironment _env;
    private readonly UpdateManager _updateManager;

    public AdminController(Database db, AshServer.AI.BackendManager backends, IConfiguration config,
        AshServer.Personality.PersonalityLoader personality, AshServer.Plugins.PluginManager plugins,
        ILogger<AdminController> log, IWebHostEnvironment env, UpdateManager updateManager)
    {
        _db = db;
        _backends = backends;
        _config = config;
        _personality = personality;
        _plugins = plugins;
        _log = log;
        _env = env;
        _updateManager = updateManager;
    }

    private string ConfigPath => Path.Combine(_env.ContentRootPath, "config.json");

    private bool IsAdmin => User.FindFirstValue("is_admin") == "true";

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        if (!IsAdmin) return Forbid();
        var totalUsers   = await _db.CountUsers();
        var totalConvs   = await _db.CountConversations();
        var totalMsgs    = await _db.CountMessages();
        var recentSignups = await _db.CountRecentUsers(7);

        var personalityDir    = _config["PersonalityDir"] ?? "personality";
        var aiName            = _personality.AiName ?? "Ash";

        // Pull the active model from the backend (same fallback logic as chat)
        var allModels    = await _backends.ListAllModels();
        var activeModel  = allModels.Count > 0 ? allModels[0].Name : (_config["DefaultModel"] ?? "");
        var backendName  = allModels.Count > 0 ? allModels[0].BackendName : "none";

        return Ok(new
        {
            total_users          = totalUsers,
            total_conversations  = totalConvs,
            total_messages       = totalMsgs,
            recent_signups       = recentSignups,
            server = new
            {
                ai_name          = aiName,
                model            = activeModel,
                backend          = backendName,
                personality_path = personalityDir,
                plugins_loaded   = _plugins.LoadedCount,
                plugins_enabled  = _plugins.EnabledCount
            }
        });
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        if (!IsAdmin) return Forbid();
        var users = await _db.GetAllUsers();
        var result = new List<object>();
        foreach (var u in users)
        {
            var roles = await _db.GetUserRoleNames(u.Id);
            var perms = u.IsAdmin ? new List<string>(AshServer.Auth.Permissions.All) : new List<string>(await _db.GetUserPermissions(u.Id));
            result.Add(new { user = AuthService.ToInfo(u, roles, perms.ToList()) });
        }
        return Ok(new { users = result.Select(x => ((dynamic)x).user) });
    }

    [HttpDelete("users/{userId}")]
    public async Task<IActionResult> DeleteUser(int userId)
    {
        if (!IsAdmin) return Forbid();
        await _db.DeleteUser(userId);
        return Ok(new { ok = true });
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] AdminCreateUserRequest req)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "username, email, and password are required" });

        var existing = await _db.GetUserByUsername(req.Username);
        if (existing != null)
            return Conflict(new { error = $"Username '{req.Username}' is already taken" });

        var hash = BCrypt.Net.BCrypt.HashPassword(req.Password);
        var user = await _db.CreateUser(req.Username, hash, req.Email, req.IsAdmin);
        return Ok(new { ok = true, id = user.Id, username = req.Username });
    }

    [HttpPost("users/{userId}/toggle-admin")]
    public async Task<IActionResult> ToggleAdmin(int userId, [FromBody] Dictionary<string, bool> body)
    {
        if (!IsAdmin) return Forbid();
        await _db.ToggleAdmin(userId, body.GetValueOrDefault("is_admin"));
        return Ok(new { ok = true });
    }

    [HttpGet("backends")]
    public async Task<IActionResult> GetBackends()
    {
        if (!IsAdmin) return Forbid();
        return Ok(await _db.GetAllBackends());
    }

    [HttpPost("backends")]
    public async Task<IActionResult> CreateBackend([FromBody] BackendCreateRequest req)
    {
        if (!IsAdmin) return Forbid();
        var backend = await _db.CreateBackend(req.Name, req.Type, req.BaseUrl, req.ApiKey);
        _backends.Invalidate();
        return Ok(backend);
    }

    [HttpPatch("backends/{id}")]
    public async Task<IActionResult> UpdateBackend(int id, [FromBody] BackendUpdateRequest req)
    {
        if (!IsAdmin) return Forbid();
        await _db.UpdateBackend(id, req.Name, req.BaseUrl, req.ApiKey);
        _backends.Invalidate();
        return Ok(new { ok = true });
    }

    [HttpDelete("backends/{id}")]
    public async Task<IActionResult> DeleteBackend(int id)
    {
        if (!IsAdmin) return Forbid();
        await _db.DeleteBackend(id);
        _backends.Invalidate();
        return Ok(new { ok = true });
    }

    [HttpPost("backends/{id}/toggle")]
    public async Task<IActionResult> ToggleBackend(int id, [FromBody] Dictionary<string, bool> body)
    {
        if (!IsAdmin) return Forbid();
        await _db.ToggleBackend(id, body.GetValueOrDefault("enabled", true));
        _backends.Invalidate();
        return Ok(new { ok = true });
    }

    [HttpPost("backends/{id}/test")]
    public async Task<IActionResult> TestBackend(int id)
    {
        if (!IsAdmin) return Forbid();
        var all = await _db.GetAllBackends();
        var row = all.FirstOrDefault(b => b.Id == id);
        if (row == null) return NotFound();
        try
        {
            AshServer.AI.IAiBackend backend = row.Type == "openai"
                ? new AshServer.AI.OpenAiCompatBackend(row.BaseUrl, row.ApiKey)
                : new AshServer.AI.OllamaBackend(row.BaseUrl);
            var models = await backend.ListModels();
            return Ok(new { ok = true, models });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[backend-test] Connection test failed for backend {Id}", id);
            return Ok(new { ok = false, error = "Backend connection failed — check URL and API key." });
        }
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics()
    {
        if (!IsAdmin) return Forbid();
        return Ok(new
        {
            total_messages = await _db.CountMessages(),
            total_conversations = await _db.CountConversations(),
            total_users = await _db.CountUsers(),
            messages_today = await _db.CountMessagesToday(),
            active_users_week = await _db.CountActiveUsersInDays(7)
        });
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetAdminConfig()
    {
        if (!IsAdmin) return Forbid();
        var path = ConfigPath;
        if (System.IO.File.Exists(path))
        {
            try
            {
                var content = await System.IO.File.ReadAllTextAsync(path);
                return Content(content, "application/json");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to read config.json");
            }
        }

        // Fallback: build a complete object from the loaded IConfiguration
        var fallbackConfig = new
        {
            server = new
            {
                host = _config["Host"] ?? "0.0.0.0",
                port = _config.GetValue("Port", 18799),
                require_auth = _config.GetValue("RequireAuth", true),
                allow_registration = _config.GetValue("AllowRegistration", true)
            },
            ai = new
            {
                model = _config["DefaultModel"] ?? "",
                temperature = _config.GetValue("DefaultTemperature", 0.7)
            },
            database = new
            {
                path = _config["DatabasePath"] ?? "ash_server.db"
            },
            uploads = new
            {
                directory = _config["UploadsDir"] ?? "uploads",
                max_size_mb = _config.GetValue("MaxUploadSizeMb", 10)
            },
            auth = new
            {
                token_expiry_hours = _config.GetValue("TokenExpiryHours", 24)
            },
            personality = new
            {
                path = _config["PersonalityDir"] ?? "personality"
            }
        };

        return Ok(fallbackConfig);
    }

    [HttpPost("config")]
    public async Task<IActionResult> SaveAdminConfig([FromBody] System.Text.Json.Nodes.JsonObject body)
    {
        if (!IsAdmin) return Forbid();
        var path = ConfigPath;
        System.Text.Json.Nodes.JsonObject cfgRoot;
        if (System.IO.File.Exists(path))
        {
            try { cfgRoot = System.Text.Json.Nodes.JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path))!.AsObject(); }
            catch { cfgRoot = new System.Text.Json.Nodes.JsonObject(); }
        }
        else { cfgRoot = new System.Text.Json.Nodes.JsonObject(); }

        // Merge the submitted keys into the config overlay
        foreach (var kvp in body)
            cfgRoot[kvp.Key] = kvp.Value?.DeepClone();

        await System.IO.File.WriteAllTextAsync(path,
            cfgRoot.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return Ok(new { ok = true, note = "Saved to config.json — some changes require restart." });
    }

    [HttpGet("backends/detect")]
    public async Task<IActionResult> DetectBackends()
    {
        if (!IsAdmin) return Forbid();

        var ollamaUrl = "http://localhost:11434";
        bool ollamaDetected;
        List<string> ollamaModels;

        try
        {
            var ollama = new AshServer.AI.OllamaBackend(ollamaUrl);
            ollamaModels  = await ollama.ListModels();
            ollamaDetected = true;
        }
        catch
        {
            ollamaModels  = [];
            ollamaDetected = false;
        }

        return Ok(new
        {
            any_detected = ollamaDetected,
            backends = new[]
            {
                new { type = "ollama", url = ollamaUrl, detected = ollamaDetected, models = ollamaModels }
            }
        });
    }

    [HttpGet("ollama/models")]
    public async Task<IActionResult> OllamaModels()
    {
        if (!IsAdmin) return Forbid();
        try
        {
            var ollama = new AshServer.AI.OllamaBackend("http://localhost:11434");
            var models = await ollama.ListModels();
            return Ok(new { models });
        }
        catch (Exception ex)
        {
            return Ok(new { models = Array.Empty<string>(), error = ex.Message });
        }
    }

    [HttpGet("plugins")]
    public IActionResult GetPlugins()
    {
        if (!IsAdmin) return Forbid();
        var list = _plugins.Plugins.Select(p => new
        {
            p.Id, p.Name, p.Version, p.Description, p.Enabled, p.Builtin,
            tool_count = p.Tools.Count,
            tools = p.Tools.Select(t => new { t.Name, t.Description, handler_type = t.Handler.Type })
        });
        return Ok(new { plugins = list });
    }

    [HttpPost("plugins/{id}/toggle")]
    public IActionResult TogglePlugin(string id)
    {
        if (!IsAdmin) return Forbid();
        var plugin = _plugins.Plugins.FirstOrDefault(p => p.Id == id);
        if (plugin == null) return NotFound(new { error = "Plugin not found" });
        if (plugin.Builtin) return BadRequest(new { error = "Built-in plugins cannot be toggled" });
        _plugins.SetEnabled(id, !plugin.Enabled);
        return Ok(new { ok = true, id, enabled = plugin.Enabled });
    }

    [HttpPost("plugins/reload")]
    public IActionResult ReloadPlugins()
    {
        if (!IsAdmin) return Forbid();
        _plugins.Reload();
        return Ok(new { ok = true, loaded = _plugins.LoadedCount, enabled = _plugins.EnabledCount });
    }

    [HttpPost("backup")]
    public IActionResult Backup()
    {
        if (!IsAdmin) return Forbid();
        var dbPath = _config["DatabasePath"] ?? "ash_server.db";
        var fullPath = Path.IsPathRooted(dbPath) ? dbPath : Path.Combine(AppContext.BaseDirectory, dbPath);
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "Database file not found" });
        var backupName = $"ash_server_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db";
        var backupPath = Path.Combine(Path.GetDirectoryName(fullPath)!, backupName);
        System.IO.File.Copy(fullPath, backupPath);
        return Ok(new { ok = true, file = backupName });
    }

    // ── Roles CRUD ──────────────────────────────────────────────────────────

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles()
    {
        if (!IsAdmin) return Forbid();
        var roles = await _db.GetRoles();
        return Ok(new { roles });
    }

    [HttpGet("roles/{id:int}")]
    public async Task<IActionResult> GetRole(int id)
    {
        if (!IsAdmin) return Forbid();
        var role = await _db.GetRole(id);
        return role == null ? NotFound() : Ok(role);
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] RoleCreateRequest req)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Role name is required" });

        // Validate permissions
        var validPerms = req.Permissions?.Where(p => AshServer.Auth.Permissions.All.Contains(p)).ToList() ?? [];
        var role = await _db.CreateRole(req.Name.Trim(), req.Description ?? "", req.Color ?? "#6366f1", validPerms);
        return Ok(new { ok = true, role });
    }

    [HttpPut("roles/{id:int}")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] RoleUpdateRequest req)
    {
        if (!IsAdmin) return Forbid();
        var existing = await _db.GetRole(id);
        if (existing == null) return NotFound();

        var validPerms = req.Permissions?.Where(p => AshServer.Auth.Permissions.All.Contains(p)).ToList();
        await _db.UpdateRole(id, req.Name, req.Description, req.Color, validPerms);
        return Ok(new { ok = true });
    }

    [HttpDelete("roles/{id:int}")]
    public async Task<IActionResult> DeleteRole(int id)
    {
        if (!IsAdmin) return Forbid();
        var existing = await _db.GetRole(id);
        if (existing == null) return NotFound();
        if (existing.IsSystem) return BadRequest(new { error = "System roles cannot be deleted" });
        await _db.DeleteRole(id);
        return Ok(new { ok = true });
    }

    [HttpGet("permissions")]
    public IActionResult ListPermissions()
    {
        if (!IsAdmin) return Forbid();
        var perms = AshServer.Auth.Permissions.All.Select(p => new
        {
            id = p,
            label = AshServer.Auth.Permissions.Labels.GetValueOrDefault(p, p)
        });
        return Ok(new { permissions = perms });
    }

    // ── User ↔ Role assignment ───────────────────────────────────────────────

    [HttpPost("users/{userId:int}/roles")]
    public async Task<IActionResult> AssignRole(int userId, [FromBody] Dictionary<string, int> body)
    {
        if (!IsAdmin) return Forbid();
        if (!body.TryGetValue("role_id", out var roleId))
            return BadRequest(new { error = "role_id required" });
        var role = await _db.GetRole(roleId);
        if (role == null) return NotFound(new { error = "Role not found" });
        await _db.AssignRole(userId, roleId);
        return Ok(new { ok = true });
    }

    [HttpDelete("users/{userId:int}/roles/{roleId:int}")]
    public async Task<IActionResult> RemoveRole(int userId, int roleId)
    {
        if (!IsAdmin) return Forbid();
        if (roleId == 1) return BadRequest(new { error = "Cannot remove the default 'user' role" });
        await _db.RemoveRole(userId, roleId);
        return Ok(new { ok = true });
    }

    [HttpGet("updates/check")]
    public async Task<IActionResult> CheckForUpdates()
    {
        if (!IsAdmin) return Forbid();
        var result = await _updateManager.CheckForUpdatesAsync();
        return Ok(new {
            has_update = result.HasUpdate,
            current_version = result.CurrentVersion,
            latest_version = result.LatestVersion,
            release_notes = result.ReleaseNotes,
            download_url = result.DownloadUrl,
            public_exposure_detected = Program.PublicExposureDetected
        });
    }

    [HttpGet("network/mesh")]
    public async Task<IActionResult> GetMeshNetworkStatus()
    {
        if (!IsAdmin) return Forbid();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tailscale",
                Arguments = "ip -4",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0)
                {
                    var ip = (await proc.StandardOutput.ReadToEndAsync()).Trim();

                    // Get hostname / tailnet domain if possible
                    var tailnet = "";
                    var deviceName = "";
                    try
                    {
                        var statusPsi = new ProcessStartInfo
                        {
                            FileName = "tailscale",
                            Arguments = "status --json",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var statusProc = Process.Start(statusPsi);
                        if (statusProc != null)
                        {
                            await statusProc.WaitForExitAsync();
                            if (statusProc.ExitCode == 0)
                            {
                                var json = await statusProc.StandardOutput.ReadToEndAsync();
                                using var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("Self", out var self))
                                {
                                    if (self.TryGetProperty("DNSName", out var dnsName))
                                        tailnet = dnsName.GetString()?.TrimEnd('.');
                                    if (self.TryGetProperty("HostName", out var hostName))
                                        deviceName = hostName.GetString();
                                }
                            }
                        }
                    }
                    catch { }

                    return Ok(new {
                        active = true,
                        provider = "tailscale",
                        ip = ip,
                        tailnet = tailnet,
                        device_name = deviceName
                    });
                }
            }
        }
        catch { }

        return Ok(new { active = false, provider = "none", ip = "", tailnet = "", device_name = "" });
    }

    [HttpPost("updates/apply")]
    public async Task<IActionResult> ApplyUpdate([FromBody] Dictionary<string, string> body)
    {
        if (!IsAdmin) return Forbid();
        if (!body.TryGetValue("download_url", out var downloadUrl) || string.IsNullOrWhiteSpace(downloadUrl))
            return BadRequest(new { error = "download_url is required" });

        // Run in background task to respond 200 OK before server restart sequence stops the process.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000);
                await _updateManager.ApplyUpdateAsync(downloadUrl);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to apply update from {Url}", downloadUrl);
            }
        });

        return Ok(new { ok = true, message = "Update started. The server will restart shortly." });
    }
}

// ── MCP Controller ─────────────────────────────────────────────────────────

[ApiController]
[Route("api/mcp")]
[Authorize]
public class McpController : ControllerBase
{
    private readonly McpManager _mcp;
    private readonly Database   _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private const string RegistryUrl = "https://raw.githubusercontent.com/ssfdre38/mcp-registry/master/registry.json";

    public McpController(McpManager mcp, Database db, IHttpClientFactory httpClientFactory)
    {
        _mcp = mcp;
        _db = db;
        _httpClientFactory = httpClientFactory;
    }

    private bool IsAdmin => User.FindFirstValue("is_admin") == "true";

    /// <summary>Lists all configured MCP servers and their connection status/tools.</summary>
    [HttpGet("servers")]
    public IActionResult ListServers()
    {
        var servers = _mcp.GetServerInfos();
        return Ok(new
        {
            servers,
            total       = servers.Count,
            connected   = servers.Count(s => s.Connected),
            total_tools = servers.Sum(s => s.ToolCount)
        });
    }

    [HttpPost("servers")]
    public async Task<IActionResult> CreateServer([FromBody] McpServerCreateRequest req)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "Name is required" });

        var id = string.IsNullOrWhiteSpace(req.Id)
            ? Guid.NewGuid().ToString("N")[..8]
            : req.Id.Trim().ToLower().Replace(' ', '-');

        var config = new McpServerConfig
        {
            Id      = id,
            Name    = req.Name.Trim(),
            Type    = req.Type is "http" or "stdio" ? req.Type : "stdio",
            Command = req.Command?.Trim() ?? "",
            Args    = req.Args ?? [],
            Env     = req.Env ?? new(),
            Url     = req.Url?.Trim() ?? "",
            Enabled = req.Enabled,
        };

        var connected = await _mcp.AddServerAsync(config);
        return Ok(new { ok = true, id, connected });
    }

    [HttpPut("servers/{id}")]
    public async Task<IActionResult> UpdateServer(string id, [FromBody] McpServerCreateRequest req)
    {
        if (!IsAdmin) return Forbid();
        var existing = await _db.GetMcpServer(id);
        if (existing is null) return NotFound();

        var config = new McpServerConfig
        {
            Id      = id,
            Name    = req.Name?.Trim() ?? existing.Name,
            Type    = req.Type is "http" or "stdio" ? req.Type : existing.Type,
            Command = req.Command?.Trim() ?? existing.Command,
            Args    = req.Args ?? existing.Args,
            Env     = req.Env ?? existing.Env,
            Url     = req.Url?.Trim() ?? existing.Url,
            Enabled = req.Enabled,
        };

        var connected = await _mcp.UpdateServerAsync(config);
        return Ok(new { ok = true, connected });
    }

    [HttpDelete("servers/{id}")]
    public async Task<IActionResult> DeleteServer(string id)
    {
        if (!IsAdmin) return Forbid();
        var existing = await _db.GetMcpServer(id);
        if (existing is null) return NotFound();
        await _mcp.DeleteServerAsync(id);
        return Ok(new { ok = true });
    }

    [HttpPost("servers/{id}/toggle")]
    public async Task<IActionResult> ToggleServer(string id, [FromBody] McpToggleRequest req)
    {
        if (!IsAdmin) return Forbid();
        var connected = await _mcp.ToggleServerAsync(id, req.Enabled);
        return Ok(new { ok = true, enabled = req.Enabled, connected });
    }

    [HttpPost("servers/{id}/reconnect")]
    public async Task<IActionResult> ReconnectServer(string id)
    {
        if (!IsAdmin) return Forbid();
        var connected = await _mcp.ReconnectAsync(id);
        return Ok(new { ok = true, connected });
    }

    [HttpGet("registry")]
    public async Task<IActionResult> GetRegistry()
    {
        if (!IsAdmin) return Forbid();

        string json = "";
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AshServer-Agent/1.0");
            json = await client.GetStringAsync(RegistryUrl);
        }
        catch
        {
            var fallbackPath = @"C:\Users\admin\.gemini\antigravity\scratch\mcp-registry\registry.json";
            if (System.IO.File.Exists(fallbackPath))
            {
                json = await System.IO.File.ReadAllTextAsync(fallbackPath);
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return BadRequest(new { error = "Failed to fetch MCP registry. Make sure you have internet access." });
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var serversEl = root.GetProperty("servers");
            
            var activeServers = _mcp.GetServerInfos();

            var resultList = new List<object>();
            foreach (var item in serversEl.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString() ?? "";
                var active = activeServers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                
                resultList.Add(new
                {
                    id = id,
                    name = item.GetProperty("name").GetString() ?? "",
                    description = item.GetProperty("description").GetString() ?? "",
                    type = item.GetProperty("type").GetString() ?? "stdio",
                    command = item.GetProperty("command").GetString() ?? "",
                    args = item.GetProperty("args").Clone(),
                    env_variables = item.GetProperty("env_variables").Clone(),
                    installed = active != null,
                    enabled = active?.Connected ?? false
                });
            }

            return Ok(new { servers = resultList });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to parse registry data: {ex.Message}" });
        }
    }

    [HttpPost("registry/install")]
    public async Task<IActionResult> InstallApp([FromBody] McpInstallRequest req)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "id and name are required" });

        var env = new Dictionary<string, string>();
        if (req.EnvVariables != null)
        {
            foreach (var kvp in req.EnvVariables)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                {
                    env[kvp.Key] = kvp.Value.Trim();
                }
            }
        }

        var args = new List<string>();
        if (req.Args != null)
        {
            foreach (var arg in req.Args)
            {
                var processed = arg;
                if (req.EnvVariables != null)
                {
                    foreach (var kvp in req.EnvVariables)
                    {
                        processed = processed.Replace($"{{{kvp.Key}}}", kvp.Value ?? "");
                    }
                }
                args.Add(processed);
            }
        }

        var config = new McpServerConfig
        {
            Id = req.Id.Trim().ToLower().Replace(' ', '-'),
            Name = req.Name.Trim(),
            Type = req.Type is "http" or "stdio" ? req.Type : "stdio",
            Command = req.Command?.Trim() ?? "",
            Args = args,
            Env = env,
            Url = req.Url?.Trim() ?? "",
            Enabled = true
        };

        var connected = await _mcp.AddServerAsync(config);
        return Ok(new { ok = true, id = config.Id, connected });
    }
}

public record McpToggleRequest(bool Enabled);

// ── Identity Controller ─────────────────────────────────────────────────────

[ApiController]
[Route("api/admin")]
[Authorize]
public class IdentityController : ControllerBase
{
    private readonly Database _db;
    private bool IsAdmin => User.FindFirstValue("is_admin") == "true";

    public IdentityController(Database db) => _db = db;

    // ── External identity management ─────────────────────────────────────

    [HttpGet("users/{userId:int}/identities")]
    public async Task<IActionResult> GetIdentities(int userId)
    {
        if (!IsAdmin) return Forbid();
        var identities = await _db.GetIdentitiesForUser(userId);
        return Ok(new { identities });
    }

    [HttpPost("users/{userId:int}/identities")]
    public async Task<IActionResult> LinkIdentity(int userId, [FromBody] AdminLinkRequest req)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Provider) || string.IsNullOrWhiteSpace(req.ExternalId))
            return BadRequest(new { error = "provider and external_id are required" });

        var identity = await _db.AddIdentity(userId, req.Provider.Trim().ToLower(), req.ExternalId.Trim(), req.ExternalUsername?.Trim());
        return Ok(new { ok = true, identity });
    }

    [HttpDelete("identities/{id:int}")]
    public async Task<IActionResult> UnlinkIdentity(int id)
    {
        if (!IsAdmin) return Forbid();
        await _db.RemoveIdentity(id);
        return Ok(new { ok = true });
    }

    // ── Channel configs ───────────────────────────────────────────────────

    [HttpGet("channels")]
    public async Task<IActionResult> GetChannels()
    {
        if (!IsAdmin) return Forbid();
        var channels = await _db.GetChannelConfigs();
        var roles    = await _db.GetRoles();
        return Ok(new { channels, roles });
    }

    [HttpPost("channels")]
    public async Task<IActionResult> UpsertChannel([FromBody] ChannelConfigRequest req)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Provider) || string.IsNullOrWhiteSpace(req.ChannelId))
            return BadRequest(new { error = "provider and channel_id are required" });

        var cfg = new ChannelConfig(0, req.Provider.Trim().ToLower(), req.GuildId?.Trim(),
            req.ChannelId.Trim(), req.Label?.Trim(), req.Enabled, req.AllowUnlinked,
            req.UnlinkedRoleId, req.AgentEnabled, req.MaxTurns,
            req.ToolAllowlist ?? [], "");
        var saved = await _db.UpsertChannelConfig(cfg);
        return Ok(new { ok = true, channel = saved });
    }

    [HttpDelete("channels/{id:int}")]
    public async Task<IActionResult> DeleteChannel(int id)
    {
        if (!IsAdmin) return Forbid();
        await _db.DeleteChannelConfig(id);
        return Ok(new { ok = true });
    }

    // ── Audit log ─────────────────────────────────────────────────────────

    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit([FromQuery] string? provider, [FromQuery] string? channel_id, [FromQuery] int limit = 100)
    {
        if (!IsAdmin) return Forbid();
        var entries = await _db.GetAuditLog(provider, channel_id, Math.Min(limit, 500));
        return Ok(new { entries });
    }
}

// ── Self-Link Controller (authenticated users) ──────────────────────────────

[ApiController]
[Route("api/auth")]
[Authorize]
public class LinkController : ControllerBase
{
    private readonly Database _db;
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public LinkController(Database db) => _db = db;

    [HttpPost("link/request")]
    public async Task<IActionResult> RequestLinkCode([FromBody] LinkCodeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Provider))
            return BadRequest(new { error = "provider is required" });

        var provider   = req.Provider.Trim().ToLower();
        var code       = Guid.NewGuid().ToString("N")[..12].ToUpper();
        var expiresAt  = DateTime.UtcNow.AddMinutes(10);
        await _db.SaveLinkCode(code, UserId, provider, expiresAt);

        var instructions = provider switch {
            "discord" => $"In Discord, run: /link {code}",
            "slack"   => $"In Slack, run: /ash-link {code}",
            _         => $"Send this code to the bot: {code}"
        };

        return Ok(new LinkCodeResponse(code, expiresAt.ToString("o"), instructions));
    }

    [HttpGet("identities")]
    public async Task<IActionResult> MyIdentities()
    {
        var identities = await _db.GetIdentitiesForUser(UserId);
        return Ok(new { identities });
    }

    [HttpDelete("identities/{id:int}")]
    public async Task<IActionResult> UnlinkSelf(int id)
    {
        var identities = await _db.GetIdentitiesForUser(UserId);
        if (!identities.Any(i => i.Id == id))
            return NotFound(new { error = "Identity not found or not yours" });
        await _db.RemoveIdentity(id);
        return Ok(new { ok = true });
    }
}

// ── Bot Link Confirm (called by external bots, no user auth) ───────────────

[ApiController]
[Route("api/bot")]
public class BotController : ControllerBase
{
    private readonly Database _db;
    private readonly IConfiguration _config;

    public BotController(Database db, IConfiguration config) { _db = db; _config = config; }

    // Bot authenticates with a shared secret from appsettings
    private bool IsBotAuthorized()
    {
        var secret = _config["Bot:Secret"];
        if (string.IsNullOrWhiteSpace(secret)) return false;
        Request.Headers.TryGetValue("X-Bot-Secret", out var provided);
        return provided == secret;
    }

    /// <summary>
    /// Called by the Discord/Slack bot when a user submits their link code.
    /// Confirms identity link: code + external_id → links to the user who generated the code.
    /// </summary>
    [HttpPost("link/confirm")]
    public async Task<IActionResult> ConfirmLink([FromBody] LinkConfirmRequest req)
    {
        if (!IsBotAuthorized()) return Unauthorized(new { error = "Invalid bot secret" });
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.ExternalId))
            return BadRequest(new { error = "code and external_id are required" });

        var result = await _db.ConsumeLinkCode(req.Code.Trim().ToUpper());
        if (result is null)
            return BadRequest(new { error = "Code is invalid, expired, or already used" });

        var (userId, provider) = result.Value;
        var identity = await _db.AddIdentity(userId, provider, req.ExternalId.Trim(), req.ExternalUsername?.Trim());
        return Ok(new { ok = true, user_id = userId, provider, identity });
    }
}

// ── Chat Providers Controller ───────────────────────────────────────────────

[ApiController]
[Route("api/admin/chat-providers")]
[Authorize]
public class ChatProvidersController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private bool IsAdmin => User.FindFirstValue("is_admin") == "true";

    private static readonly string MaskPlaceholder = "••••";

    public ChatProvidersController(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    private string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    private static string MaskToken(string? val) =>
        string.IsNullOrEmpty(val) ? "" : MaskPlaceholder + val[^Math.Min(4, val.Length)..];

    private static bool IsMasked(string? val) =>
        val != null && val.StartsWith(MaskPlaceholder);

    [HttpGet]
    public IActionResult GetProviders()
    {
        if (!IsAdmin) return Forbid();

        var s = _config.GetSection("ThirdPartyChat");
        return Ok(new
        {
            bot_link_secret   = MaskToken(s["BotLinkSecret"] ?? _config["Bot:Secret"]),
            discord = new {
                enabled         = s.GetValue("Discord:Enabled", false),
                bot_token       = MaskToken(s["Discord:BotToken"]),
                application_id  = s["Discord:ApplicationId"] ?? "",
                command_prefix  = s["Discord:CommandPrefix"] ?? "!",
                status_text     = s["Discord:StatusText"] ?? "",
            },
            slack = new {
                enabled         = s.GetValue("Slack:Enabled", false),
                bot_token       = MaskToken(s["Slack:BotToken"]),
                app_token       = MaskToken(s["Slack:AppToken"]),
                signing_secret  = MaskToken(s["Slack:SigningSecret"]),
            },
            telegram = new {
                enabled         = s.GetValue("Telegram:Enabled", false),
                bot_token       = MaskToken(s["Telegram:BotToken"]),
                webhook_url     = s["Telegram:WebhookUrl"] ?? "",
            },
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveProviders([FromBody] SaveThirdPartyChatRequest req)
    {
        if (!IsAdmin) return Forbid();

        var path = ConfigPath;
        // Bootstrap config.json from appsettings.json if it doesn't exist yet
        if (!System.IO.File.Exists(path))
        {
            var appSettingsPath = Path.Combine(_env.ContentRootPath, "appsettings.json");
            if (System.IO.File.Exists(appSettingsPath))
                System.IO.File.Copy(appSettingsPath, path);
            else
                await System.IO.File.WriteAllTextAsync(path, "{}");
        }

        // Read current file as JsonNode so we can merge without losing other keys
        var raw  = await System.IO.File.ReadAllTextAsync(path);
        var root = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();

        if (!root.ContainsKey("ThirdPartyChat"))
            root["ThirdPartyChat"] = new System.Text.Json.Nodes.JsonObject();

        var tpc = root["ThirdPartyChat"]!.AsObject();

        // Bot link secret (also keep Bot:Secret in sync for BotController)
        if (!string.IsNullOrEmpty(req.BotLinkSecret) && !IsMasked(req.BotLinkSecret))
        {
            tpc["BotLinkSecret"] = req.BotLinkSecret;
            if (root.ContainsKey("Bot"))
                root["Bot"]!.AsObject()["Secret"] = req.BotLinkSecret;
        }

        // Discord
        if (req.Discord is { } d)
        {
            if (!tpc.ContainsKey("Discord")) tpc["Discord"] = new System.Text.Json.Nodes.JsonObject();
            var disc = tpc["Discord"]!.AsObject();
            disc["Enabled"]       = d.Enabled;
            if (!IsMasked(d.BotToken))      disc["BotToken"]      = d.BotToken ?? "";
            if (!string.IsNullOrEmpty(d.ApplicationId)) disc["ApplicationId"] = d.ApplicationId;
            disc["CommandPrefix"] = d.CommandPrefix ?? "!";
            disc["StatusText"]    = d.StatusText ?? "";
        }

        // Slack
        if (req.Slack is { } sl)
        {
            if (!tpc.ContainsKey("Slack")) tpc["Slack"] = new System.Text.Json.Nodes.JsonObject();
            var slack = tpc["Slack"]!.AsObject();
            slack["Enabled"]       = sl.Enabled;
            if (!IsMasked(sl.BotToken))      slack["BotToken"]      = sl.BotToken ?? "";
            if (!IsMasked(sl.AppToken))      slack["AppToken"]      = sl.AppToken ?? "";
            if (!IsMasked(sl.SigningSecret)) slack["SigningSecret"] = sl.SigningSecret ?? "";
        }

        // Telegram
        if (req.Telegram is { } tg)
        {
            if (!tpc.ContainsKey("Telegram")) tpc["Telegram"] = new System.Text.Json.Nodes.JsonObject();
            var tel = tpc["Telegram"]!.AsObject();
            tel["Enabled"]    = tg.Enabled;
            if (!IsMasked(tg.BotToken)) tel["BotToken"]   = tg.BotToken ?? "";
            tel["WebhookUrl"] = tg.WebhookUrl ?? "";
        }

        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        await System.IO.File.WriteAllTextAsync(path, root.ToJsonString(opts));

        return Ok(new { ok = true, note = "Saved to appsettings.json. Restart server to apply connection changes." });
    }
}

// ── Health endpoint (public — no auth required) ────────────────────────────

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private static readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    private readonly Database _db;
    private readonly BackendManager _backends;
    private readonly IConfiguration _config;

    public HealthController(Database db, BackendManager backends, IConfiguration config)
    {
        _db = db; _backends = backends; _config = config;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get()
    {
        var uptime = (DateTimeOffset.UtcNow - _startTime).TotalSeconds;

        // DB check
        bool dbOk;
        try { await _db.GetUserById(0); dbOk = true; }
        catch { dbOk = false; }

        // Backend summary — only expose count and status, not URLs or keys
        var allBackends = await _db.GetAllBackends();
        var backendList = allBackends.Select(b => (object)new
        {
            name = b.Name,
            type = b.Type
            // url intentionally omitted — do not expose in public health endpoint
        }).ToList();

        // Discord status
        var discordEnabled = _config.GetValue("ThirdPartyChat:Discord:Enabled", false);

        var status = dbOk ? "ok" : "degraded";

        return Ok(new
        {
            status,
            uptime_seconds = (int)uptime,
            database = dbOk ? "ok" : "error",
            backends = new { count = allBackends.Count, items = backendList },
            integrations = new
            {
                discord = new { enabled = discordEnabled }
            },
            process = new
            {
                ram_mb       = (int)(Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024),
                thread_count = Process.GetCurrentProcess().Threads.Count
            }
        });
    }
}

public record McpInstallRequest(
    string Id,
    string Name,
    string Type,
    string? Command,
    List<string>? Args,
    Dictionary<string, string>? EnvVariables,
    string? Url
);
