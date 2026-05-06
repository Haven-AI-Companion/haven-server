using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AshServer.Auth;
using AshServer.Data;
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
        return Ok(new LoginResponse(token, AuthService.ToInfo(user!)));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromForm] LoginRequest req)
    {
        var (user, error) = await _auth.Login(req.Username, req.Password);
        if (error != null) return Unauthorized(new { error });

        var token = _auth.GenerateToken(user!);
        return Ok(new LoginResponse(token, AuthService.ToInfo(user!)));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.GetUserById(userId);
        if (user == null) return Unauthorized();
        return Ok(new { user = AuthService.ToInfo(user) });
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

    public ModelsController(AshServer.AI.BackendManager backends, IConfiguration config, IWebHostEnvironment env)
    {
        _backends = backends;
        _config = config;
        _env = env;
    }

    [HttpGet("models")]
    public async Task<IActionResult> ListModels() =>
        Ok(await _backends.ListAllModels());

    [HttpGet("config")]
    public IActionResult GetConfig() => Ok(new
    {
        default_model = _config["DefaultModel"] ?? ""
    });

    [HttpGet("plugins")]
    public IActionResult ListPlugins() => Ok(new { plugins = Array.Empty<object>() });

    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50 MB
    public async Task<IActionResult> Upload(IFormFile file)
    {
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

        // For images, also return base64 for vision models
        string? base64 = null;
        if (isImage)
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(fullPath);
            base64 = Convert.ToBase64String(bytes);
        }

        return Ok(new
        {
            url,
            filename = file.FileName,
            size = file.Length,
            is_image = isImage,
            base64
        });
    }
}

[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly Database _db;
    private readonly AshServer.AI.BackendManager _backends;
    private readonly IConfiguration _config;

    public AdminController(Database db, AshServer.AI.BackendManager backends, IConfiguration config)
    {
        _db = db;
        _backends = backends;
        _config = config;
    }

    private bool IsAdmin => User.FindFirstValue("is_admin") == "true";

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        if (!IsAdmin) return Forbid();
        return Ok(new
        {
            users = await _db.CountUsers(),
            conversations = await _db.CountConversations(),
            messages = await _db.CountMessages()
        });
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        if (!IsAdmin) return Forbid();
        var users = await _db.GetAllUsers();
        return Ok(users.Select(AuthService.ToInfo));
    }

    [HttpDelete("users/{userId}")]
    public async Task<IActionResult> DeleteUser(int userId)
    {
        if (!IsAdmin) return Forbid();
        await _db.DeleteUser(userId);
        return Ok(new { ok = true });
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromForm] string username, [FromForm] string email,
        [FromForm] string password, [FromForm] bool is_admin = false)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return BadRequest(new { error = "username, email, and password are required" });

        var existing = await _db.GetUserByUsername(username);
        if (existing != null)
            return Conflict(new { error = $"Username '{username}' is already taken" });

        var hash = BCrypt.Net.BCrypt.HashPassword(password);
        var user = await _db.CreateUser(username, hash, email, is_admin);
        return Ok(new { ok = true, id = user.Id, username });
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
            messages_today = 0,
            active_users_week = await _db.CountUsers()
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
}
