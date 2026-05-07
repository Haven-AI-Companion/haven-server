using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AshServer.Auth;
using AshServer.Data;
using AshServer.Mcp;
using AshServer.Models;
using System.Security.Claims;

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

    public AdminController(Database db, AshServer.AI.BackendManager backends, IConfiguration config,
        AshServer.Personality.PersonalityLoader personality, AshServer.Plugins.PluginManager plugins)
    {
        _db = db;
        _backends = backends;
        _config = config;
        _personality = personality;
        _plugins = plugins;
    }

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
            return Ok(new { ok = false, error = ex.Message });
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
    public IActionResult GetAdminConfig()
    {
        if (!IsAdmin) return Forbid();
        return Ok(new
        {
            auth = new { require_auth = _config.GetValue("RequireAuth", true), allow_registration = _config.GetValue("AllowRegistration", true), token_expiry_hours = 24 },
            personality = new { dir = _config["PersonalityDir"] ?? "personality" },
            server = new { port = _config.GetValue("Port", 18799) }
        });
    }

    [HttpPost("config")]
    public IActionResult SaveAdminConfig([FromBody] object body)
    {
        if (!IsAdmin) return Forbid();
        // Runtime config changes not persisted in this version
        return Ok(new { ok = true, note = "Config changes require restart to take effect. Edit appsettings.json directly." });
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
}

// ── MCP Controller ─────────────────────────────────────────────────────────

[ApiController]
[Route("api/mcp")]
[Authorize]
public class McpController : ControllerBase
{
    private readonly McpManager _mcp;
    private readonly Database   _db;

    public McpController(McpManager mcp, Database db) { _mcp = mcp; _db = db; }

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
}

public record McpToggleRequest(bool Enabled);
