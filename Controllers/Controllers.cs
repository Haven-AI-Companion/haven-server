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
using System.Text;
using System.Text.Json;
using System.IO.Compression;

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

    [HttpGet("me/profile")]
    [Authorize]
    public async Task<IActionResult> GetProfile()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.GetUserById(userId);
        if (user == null) return NotFound();
        return Ok(new
        {
            displayName = user.DisplayName,
            gender = user.Gender,
            avatarPath = user.AvatarPath
        });
    }

    [HttpPost("me/profile")]
    [Authorize]
    public async Task<IActionResult> UpdateProfile([FromBody] UserProfileRequest req)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _db.UpdateUserProfile(userId, req.DisplayName, req.Gender);
        return Ok(new { ok = true });
    }

    [HttpPost("me/profile/avatar")]
    [Authorize]
    public async Task<IActionResult> UploadAvatar(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded" });

        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.GetUserById(userId);
        if (user == null) return Unauthorized();

        try
        {
            var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);

            var ext = Path.GetExtension(file.FileName).ToLower();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp")
                return BadRequest(new { error = "Invalid image format" });

            var fileName = $"user_{userId}_avatar_{DateTime.UtcNow.Ticks}{ext}";
            var filePath = Path.Combine(uploadsDir, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var webPath = $"/uploads/{fileName}";
            await _db.UpdateUserProfile(userId, user.DisplayName, user.Gender, webPath);

            return Ok(new { ok = true, avatarPath = webPath });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to upload avatar: {ex.Message}" });
        }
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
        var companionId = body?.GetValueOrDefault("companion_id");
        var id = await _db.CreateConversation(UserId, title, null, companionId);
        return Ok(new { id, title, companionId });
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

    [HttpPost("{id}/messages")]
    public async Task<IActionResult> AddMessage(string id, [FromBody] Dictionary<string, string> body)
    {
        var conv = await _db.GetConversation(id, UserId);
        if (conv == null) return NotFound();

        var role = body.GetValueOrDefault("role") ?? "assistant";
        var content = body.GetValueOrDefault("content") ?? "";

        if (string.IsNullOrEmpty(content)) return BadRequest(new { error = "content required" });

        await _db.AddMessage(id, role, content);
        return Ok(new { ok = true });
    }

    [HttpPut("{id}/messages/{messageId:int}")]
    public async Task<IActionResult> UpdateMessage(string id, int messageId, [FromBody] Dictionary<string, string> body)
    {
        var conv = await _db.GetConversation(id, UserId);
        if (conv == null) return NotFound();

        var content = body.GetValueOrDefault("content") ?? "";
        if (string.IsNullOrEmpty(content)) return BadRequest(new { error = "content required" });

        await _db.UpdateMessage(messageId, content);
        return Ok(new { ok = true });
    }

    [HttpDelete("{id}/messages/{messageId:int}")]
    public async Task<IActionResult> DeleteMessage(string id, int messageId)
    {
        var conv = await _db.GetConversation(id, UserId);
        if (conv == null) return NotFound();

        await _db.DeleteMessage(messageId);
        return Ok(new { ok = true });
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

    [HttpPost("{id}/import")]
    public async Task<IActionResult> Import(string id, IFormFile file)
    {
        var conv = await _db.GetConversation(id, UserId);
        if (conv == null) return NotFound();

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "File is required." });

        try
        {
            string content;
            using (var reader = new StreamReader(file.OpenReadStream()))
            {
                content = await reader.ReadToEndAsync();
            }

            List<(string role, string text, string? senderName)> parsedMessages = new();

            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string mes = item.TryGetProperty("mes", out var m) ? m.GetString() ?? "" : "";
                    bool isUser = item.TryGetProperty("is_user", out var iu) && iu.GetBoolean();
                    bool isSystem = item.TryGetProperty("is_system", out var isy) && isy.GetBoolean();

                    if (isSystem) continue;

                    var role = isUser ? "user" : "assistant";
                    parsedMessages.Add((role, mes, name));
                }
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in msgs.EnumerateArray())
                    {
                        string role = item.TryGetProperty("role", out var r) ? r.GetString() ?? "assistant" : "assistant";
                        string text = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        string? senderName = item.TryGetProperty("senderName", out var sn) ? sn.GetString() : null;
                        parsedMessages.Add((role, text, senderName));
                    }
                }
                else if (root.TryGetProperty("chat", out var chat) && chat.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in chat.EnumerateArray())
                    {
                        string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        string mes = item.TryGetProperty("mes", out var m) ? m.GetString() ?? "" : "";
                        bool isUser = item.TryGetProperty("is_user", out var iu) && iu.GetBoolean();

                        var role = isUser ? "user" : "assistant";
                        parsedMessages.Add((role, mes, name));
                    }
                }
            }

            if (parsedMessages.Count == 0)
            {
                return BadRequest(new { error = "No messages could be parsed from the file." });
            }

            await _db.ClearMessages(id);

            foreach (var msg in parsedMessages)
            {
                await _db.AddMessage(id, msg.role, msg.text, msg.senderName);
            }

            return Ok(new { ok = true, importedCount = parsedMessages.Count });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Import failed: {ex.Message}" });
        }
    }

    [HttpPost("{id}/import-text")]
    public async Task<IActionResult> ImportText(string id, [FromBody] Dictionary<string, string> body)
    {
        var conv = await _db.GetConversation(id, UserId);
        if (conv == null) return NotFound();

        var rawText = body.GetValueOrDefault("text") ?? "";
        if (string.IsNullOrWhiteSpace(rawText)) return BadRequest(new { error = "text required" });

        var companionName = body.GetValueOrDefault("companion_name") ?? "Companion";
        var userName = body.GetValueOrDefault("user_name") ?? "User";

        try
        {
            var lines = rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                               .Select(l => l.Trim())
                               .Where(l => !string.IsNullOrEmpty(l))
                               .ToList();

            List<(string role, string text)> parsedMessages = new();
            string? currentRole = null;
            System.Text.StringBuilder currentContent = new();

            void FlushCurrent()
            {
                if (currentRole != null && currentContent.Length > 0)
                {
                    parsedMessages.Add((currentRole, currentContent.ToString().Trim()));
                    currentContent.Clear();
                }
            }

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                // Skip timestamps
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^(?:\d{1,2}:\d{2}\s*(?:AM|PM)?|\d{4}-\d{2}-\d{2}.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;

                // Skip status headers
                if (line.Equals("Online", StringComparison.OrdinalIgnoreCase) || line.Equals("Active now", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 1. Inline speaker indicator format: "Name: message"
                var match = System.Text.RegularExpressions.Regex.Match(line, $@"^(?:{System.Text.RegularExpressions.Regex.Escape(companionName)}|{System.Text.RegularExpressions.Regex.Escape(userName)}|You|Me)\s*:\s*(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    FlushCurrent();
                    var isUser = line.StartsWith("You", StringComparison.OrdinalIgnoreCase) || 
                                 line.StartsWith("Me", StringComparison.OrdinalIgnoreCase) ||
                                 line.StartsWith(userName, StringComparison.OrdinalIgnoreCase);
                    currentRole = isUser ? "user" : "assistant";
                    currentContent.Append(match.Groups[1].Value);
                    continue;
                }

                // 2. Block speaker name indicator on its own line:
                if (line.Equals(companionName, StringComparison.OrdinalIgnoreCase))
                {
                    FlushCurrent();
                    currentRole = "assistant";
                    continue;
                }
                if (line.Equals(userName, StringComparison.OrdinalIgnoreCase) || 
                    line.Equals("You", StringComparison.OrdinalIgnoreCase) || 
                    line.Equals("Me", StringComparison.OrdinalIgnoreCase))
                {
                    FlushCurrent();
                    currentRole = "user";
                    continue;
                }

                // 3. Continuation
                if (currentRole != null)
                {
                    if (currentContent.Length > 0) currentContent.Append("\n");
                    currentContent.Append(line);
                }
                else
                {
                    currentRole = "assistant";
                    currentContent.Append(line);
                }
            }

            FlushCurrent();

            if (parsedMessages.Count == 0)
            {
                return BadRequest(new { error = "No messages could be parsed from the pasted text." });
            }

            await _db.ClearMessages(id);

            foreach (var msg in parsedMessages)
            {
                await _db.AddMessage(id, msg.role, msg.text, msg.role == "user" ? userName : companionName);
            }

            return Ok(new { ok = true, importedCount = parsedMessages.Count });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Import failed: {ex.Message}" });
        }
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
        sb.AppendLine($"> Exported from Haven Server — {conv.CreatedAt}");
        sb.AppendLine();
        foreach (var m in messages)
        {
            sb.AppendLine(m.Role == "user" ? "**You:**" : $"**{(m.SenderName ?? "Companion")}:**");
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
            sb.AppendLine($"{(m.Role == "user" ? "You" : (m.SenderName ?? "Companion"))}: {m.Content}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

[ApiController]
[Route("api")]
public class ModelsController : ControllerBase
{
    public static DateTime LastActiveChatTime = DateTime.MinValue;
    public static CancellationTokenSource? ActiveProactiveCts = null;

    public static void CancelActiveProactiveTask()
    {
        try
        {
            if (ActiveProactiveCts != null)
            {
                ActiveProactiveCts.Cancel();
                ActiveProactiveCts = null;
            }
        }
        catch { }
    }

    private readonly AshServer.AI.BackendManager _backends;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly Database _db;
    private readonly AshServer.Plugins.PluginManager _plugins;
    private readonly AshServer.Personality.PersonalityLoader _personality;
    private readonly AshServer.AI.RagService _ragService;

    public ModelsController(
        AshServer.AI.BackendManager backends, 
        IConfiguration config, 
        IWebHostEnvironment env, 
        Database db, 
        AshServer.Plugins.PluginManager plugins,
        AshServer.Personality.PersonalityLoader personality,
        AshServer.AI.RagService ragService)
    {
        _backends = backends;
        _config = config;
        _env = env;
        _db = db;
        _plugins = plugins;
        _personality = personality;
        _ragService = ragService;
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

    [HttpGet("uploads")]
    public IActionResult ListUploads()
    {
        try
        {
            var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
            if (!Directory.Exists(uploadsDir))
                return Ok(new List<string>());

            var files = new DirectoryInfo(uploadsDir).GetFiles()
                .Where(f => f.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) || 
                            f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || 
                            f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.Name)
                .ToList();

            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string _pendingDevCommand = "";
    private static string _devCommandOutput = "";

    [HttpPost("developer/command")]
    public IActionResult SetDevCommand([FromBody] DevCommandReq req)
    {
        _pendingDevCommand = req.Command ?? "";
        _devCommandOutput = "";
        return Ok(new { status = "Command queued." });
    }

    [HttpGet("developer/command")]
    public IActionResult GetDevCommand()
    {
        var cmd = _pendingDevCommand;
        _pendingDevCommand = ""; // Clear after retrieval
        return Ok(new { command = cmd });
    }

    [HttpPost("developer/output")]
    public IActionResult SetDevOutput([FromBody] DevOutputReq req)
    {
        _devCommandOutput = req.Output ?? "";
        return Ok();
    }

    [HttpGet("developer/output")]
    public IActionResult GetDevOutput()
    {
        return Ok(new { output = _devCommandOutput });
    }

    public class DevCommandReq
    {
        [System.Text.Json.Serialization.JsonPropertyName("command")]
        public string Command { get; set; } = "";
    }

    public class DevOutputReq
    {
        [System.Text.Json.Serialization.JsonPropertyName("output")]
        public string Output { get; set; } = "";
    }

    private string ExtractUserMessage(string prompt, string? displayName)
    {
        if (string.IsNullOrEmpty(prompt)) return "";

        int lastNewline = prompt.LastIndexOf('\n');
        if (lastNewline <= 0) return prompt;

        string targetPart = prompt.Substring(0, lastNewline).TrimEnd();

        if (!string.IsNullOrEmpty(displayName))
        {
            string userPrefix = $"\n{displayName}:";
            int prefixIdx = targetPart.LastIndexOf(userPrefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIdx >= 0)
            {
                return targetPart.Substring(prefixIdx + userPrefix.Length).Trim();
            }

            userPrefix = $"{displayName}:";
            if (targetPart.StartsWith(userPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return targetPart.Substring(userPrefix.Length).Trim();
            }
        }

        int lastColon = targetPart.LastIndexOf(':');
        if (lastColon > 0)
        {
            int prevNewline = targetPart.LastIndexOf('\n', lastColon);
            int nameStart = prevNewline + 1;
            if (lastColon - nameStart < 30)
            {
                return targetPart.Substring(lastColon + 1).Trim();
            }
        }

        return prompt;
    }

    [HttpPost("chat")]
    [Authorize]
    public async Task Chat([FromBody] ChatRequest req, CancellationToken cancellationToken)
    {
        LastActiveChatTime = DateTime.UtcNow;
        CancelActiveProactiveTask();

        var promptText = req.Prompt ?? "";
        Console.WriteLine($"[chat] Received request. Prompt length: {promptText.Length} chars.");

        string messageUuid;
        if (Request.Headers.TryGetValue("X-Request-UUID", out var requestUuidVal))
        {
            messageUuid = requestUuidVal.ToString();
        }
        else
        {
            messageUuid = Guid.NewGuid().ToString();
        }
        Response.Headers["X-Message-UUID"] = messageUuid;

        if (_config.GetValue("ai:DisableMemoryExtraction", false) && 
            promptText.StartsWith("Extract key facts", StringComparison.OrdinalIgnoreCase))
        {
            Response.ContentType = "text/plain";
            await Response.WriteAsync("NONE\n");
            await Response.Body.FlushAsync();
            return;
        }

        var modelId = req.Model ?? _config["DefaultModel"] ?? "";
        
        Response.ContentType = "text/plain";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["Content-Encoding"] = "identity";

        var bufferingFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();

        var username = User.FindFirstValue(ClaimTypes.Name) ?? "User";
        var systemPrompt = _personality.GetSystemPrompt(username, req.DisplayName);

        var companionName = _personality.AiName ?? "Companion";
        var compClean = string.Concat(companionName.Split(Path.GetInvalidFileNameChars())).Trim();
        var convId = string.IsNullOrWhiteSpace(req.ConversationId) ? $"char_{compClean}" : req.ConversationId;

        var convState = await _db.GetConversationState(convId);
        if (convState != null)
        {
            var stateParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(convState.Location)) stateParts.Add($"Location = {convState.Location}");
            if (!string.IsNullOrWhiteSpace(convState.Outfit)) stateParts.Add($"Outfit = {convState.Outfit}");
            if (!string.IsNullOrWhiteSpace(convState.Mood)) stateParts.Add($"Mood = {convState.Mood}");
            if (!string.IsNullOrWhiteSpace(convState.ClothingState)) stateParts.Add($"ClothingState = {convState.ClothingState}");

            if (stateParts.Count > 0)
            {
                var stateContext = $"[Companion State: {string.Join(", ", stateParts)}]";
                systemPrompt = stateContext + "\n" + systemPrompt;
                Console.WriteLine($"[chat] Injected active companion state: {stateContext}");
            }
        }

        if (promptText.StartsWith("You are ") && promptText.Contains("\n\n"))
        {
            int lastDoubleNewline = promptText.LastIndexOf("\n\n");
            if (lastDoubleNewline > 0)
            {
                systemPrompt = promptText.Substring(0, lastDoubleNewline).Trim();
                promptText = promptText.Substring(lastDoubleNewline + 2).Trim();
            }
        }

        if (!string.IsNullOrEmpty(req.ConversationId) && TopicSummarizer.ActiveTopics.TryGetValue(req.ConversationId, out var activeTopic))
        {
            systemPrompt = $"[Active Topic Context: {activeTopic}]\n" + systemPrompt;
            Console.WriteLine($"[chat] Injected active topic into system prompt: {activeTopic}");
        }

        var messages = new List<ChatMessage>
        {
            new ChatMessage("system", systemPrompt),
            new ChatMessage("user", promptText, req.Image != null ? new List<string>{ req.Image } : null)
        };

        var responseText = new System.Text.StringBuilder();
        try
        {
            await foreach (var token in _backends.StreamChat(modelId, messages, cancellationToken))
            {
                responseText.Append(token);
                // Write token + newline so pocket-ash ReadLineAsync streams it smoothly
                await Response.WriteAsync(token + "\n");
                await Response.Body.FlushAsync();
            }

            if (responseText.Length > 0)
            {
                var responseStr = responseText.ToString();
                var lowerResponse = responseStr.ToLowerInvariant();
                bool shouldGenSelfie = responseStr.Contains("<call>generate_portrait</call>");

                if (shouldGenSelfie)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                    {
                        var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
                        var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
                        var localDir = Path.Combine(baseDir, "local");
                        var localFile = Path.Combine(localDir, $"{compClean.ToLowerInvariant()}.json");
                        var baseFile = Path.Combine(baseDir, $"{compClean.ToLowerInvariant()}.json");
                        var checkFile = System.IO.File.Exists(localFile) ? localFile : (System.IO.File.Exists(baseFile) ? baseFile : null);

                        CompanionConfig? comp = null;
                        if (checkFile != null)
                        {
                            var json = await System.IO.File.ReadAllTextAsync(checkFile);
                            comp = JsonSerializer.Deserialize<CompanionConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        }

                        var details = comp?.Description ?? comp?.Personality ?? "";
                        var location = convState?.Location ?? comp?.CurrentLocation ?? "";
                        var outfit = convState?.Outfit ?? comp?.CurrentOutfit ?? "";
                        var mood = convState?.Mood ?? comp?.CurrentMood ?? "";
                        var clothing = convState?.ClothingState ?? comp?.ClothingState ?? "";
                        var bodyType = convState?.BodyType ?? comp?.BodyType ?? "";
                        var bodyShape = convState?.BodyShape ?? comp?.BodyShape ?? "";

                        var sdPrompt = $"digital art portrait of {companionName}, highly detailed";
                        if (!string.IsNullOrWhiteSpace(details)) sdPrompt += $", {details}";
                        if (!string.IsNullOrWhiteSpace(bodyType)) sdPrompt += $", body type: {bodyType}";
                        if (!string.IsNullOrWhiteSpace(bodyShape)) sdPrompt += $", body shape: {bodyShape}";
                        if (!string.IsNullOrWhiteSpace(location)) sdPrompt += $", at/in {location}";
                        if (!string.IsNullOrWhiteSpace(outfit)) sdPrompt += $", wearing {outfit}";
                        if (!string.IsNullOrWhiteSpace(mood)) sdPrompt += $", {mood} expression";
                        if (!string.IsNullOrWhiteSpace(clothing)) sdPrompt += $", {clothing}";

                        var sdArgObj = new { description = sdPrompt };
                        var sdArgElement = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(sdArgObj));
                        var relativeImagePath = await _plugins.ExecuteTool("generate_portrait", sdArgElement);

                        if (!string.IsNullOrEmpty(relativeImagePath) && relativeImagePath.StartsWith("/uploads/"))
                        {
                            var imgMarkdown = $"\n\n![Selfie]({relativeImagePath})";
                            await Response.WriteAsync(imgMarkdown + "\n");
                            await Response.Body.FlushAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[chat] Failed to auto-generate selfie: {ex.Message}");
                    }
                });
            }

                var userId = UserId;
                var conversationId = req.ConversationId;
                if (!string.IsNullOrEmpty(conversationId))
                {
                    var existing = await _db.GetConversation(conversationId, userId);
                    if (existing == null)
                    {
                        await _db.CreateConversation(userId, customId: conversationId, companionId: req.CompanionName);
                    }
                    else if (string.IsNullOrEmpty(existing.CompanionId) && !string.IsNullOrEmpty(req.CompanionName))
                    {
                        await _db.SetConversationCompanion(conversationId, req.CompanionName);
                    }
                }
                else
                {
                    conversationId = await _db.CreateConversation(userId, companionId: req.CompanionName);
                }
                
                var cleanUserMsg = ExtractUserMessage(promptText, req.DisplayName);
                await _db.AddMessage(conversationId, "user", cleanUserMsg);
                await _db.AddMessage(conversationId, "assistant", responseStr);
                
                _ = Task.Run(() => TopicSummarizer.SummarizeConversation(conversationId, _db, _backends));
            }
        }
        catch (Exception ex)
        {
            await Response.WriteAsync($"\n[ERROR] {ex.Message}\n");
            await Response.Body.FlushAsync();
        }
    }

    [HttpGet("plugins")]
    [Authorize]
    public IActionResult ListPlugins() => Ok(new
    {
        plugins = _plugins.Plugins
            .Where(p => p.Enabled)
            .Select(p => new { p.Id, p.Name, p.Description, tool_count = p.Tools.Count })
    });

    public class ExecuteToolRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("tool")]
        public string Tool { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("arguments")]
        public System.Text.Json.JsonElement Arguments { get; set; }
    }

    [HttpPost("tools/execute")]
    [Authorize]
    public async Task<IActionResult> ExecuteToolEndpoint([FromBody] ExecuteToolRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Tool))
            return BadRequest(new { error = "tool is required" });
            
        try
        {
            var result = await _plugins.ExecuteTool(req.Tool, req.Arguments);
            return Ok(new { result });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("tts/voices")]
    [AllowAnonymous]
    public IActionResult ListVoices()
    {
        var list = new System.Collections.Generic.List<object>();

        // 1. Static Kokoro voices supported by server python script
        var kokoroVoices = new[]
        {
            new { id = "af_sarah", name = "Sarah (Female, Kokoro)", type = "kokoro" },
            new { id = "af_bella", name = "Bella (Female, Kokoro)", type = "kokoro" },
            new { id = "af_nicole", name = "Nicole (Female, Kokoro)", type = "kokoro" },
            new { id = "af_sky", name = "Sky (Female, Kokoro)", type = "kokoro" },
            new { id = "am_adam", name = "Adam (Male, Kokoro)", type = "kokoro" },
            new { id = "am_michael", name = "Michael (Male, Kokoro)", type = "kokoro" },
            new { id = "bf_emma", name = "Emma (Female UK, Kokoro)", type = "kokoro" },
            new { id = "bf_isabella", name = "Isabella (Female UK, Kokoro)", type = "kokoro" },
            new { id = "bm_george", name = "George (Male UK, Kokoro)", type = "kokoro" },
            new { id = "bm_lewis", name = "Lewis (Male UK, Kokoro)", type = "kokoro" }
        };
        list.AddRange(kokoroVoices);

        // 2. Scan Piper ONNX models directory
        var modelsDir = @"C:\Users\admin\piper\piper\models";
        if (System.IO.Directory.Exists(modelsDir))
        {
            try
            {
                var files = System.IO.Directory.GetFiles(modelsDir, "*.onnx");
                foreach (var file in files)
                {
                    var id = System.IO.Path.GetFileNameWithoutExtension(file);
                    var name = id.Replace("_", " ").Replace("-", " ");
                    name = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name) + " (Piper)";
                    list.Add(new { id = id, name = name, type = "piper" });
                }
            }
            catch {}
        }

        return Ok(list);
    }

    public class TtsRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("voice")]
        public string Voice { get; set; } = "en_US-amy-medium";

        [System.Text.Json.Serialization.JsonPropertyName("companion")]
        public string? Companion { get; set; }
    }

    [HttpPost("tts")]
    [Authorize]
    public async Task<IActionResult> GenerateTts([FromBody] TtsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "Text is required" });

        try
        {
            var filename = $"tts_{Guid.NewGuid().ToString("N")[..12]}.wav";
            var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            
            string companionSubdir = "_global";
            if (!string.IsNullOrWhiteSpace(req.Companion))
            {
                companionSubdir = string.Concat(req.Companion.Split(Path.GetInvalidFileNameChars())).Trim();
                if (string.IsNullOrWhiteSpace(companionSubdir))
                {
                    companionSubdir = "_global";
                }
            }

            var userSubdir = $"user_{UserId}";
            var relativeSubpath = Path.Combine("uploads", companionSubdir, userSubdir);
            var targetDir = Path.Combine(webRoot, relativeSubpath);

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);
                
            var outputPath = Path.Combine(targetDir, filename);

            var voiceName = req.Voice ?? "en_US-amy-medium";
            bool isKokoro = voiceName.StartsWith("kokoro_") || 
                            voiceName.Equals("af_sarah") || 
                            voiceName.Equals("af_bella") || 
                            voiceName.Equals("af_nicole") || 
                            voiceName.Equals("af_sky") || 
                            voiceName.Equals("am_adam") || 
                            voiceName.Equals("am_michael") || 
                            voiceName.Equals("bf_emma") || 
                            voiceName.Equals("bf_isabella") || 
                            voiceName.Equals("bm_george") || 
                            voiceName.Equals("bm_lewis");

            if (isKokoro)
            {
                var pythonExe = @"C:\Users\admin\AppData\Local\Python\pythoncore-3.14-64\python.exe";
                var kokoroScript = @"C:\Users\admin\piper\piper\kokoro_tts.py";
                
                if (!System.IO.File.Exists(pythonExe))
                    return StatusCode(500, new { error = "python.exe was not found on the server." });
                if (!System.IO.File.Exists(kokoroScript))
                    return StatusCode(500, new { error = "kokoro_tts.py was not found on the server." });

                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{kokoroScript}\" \"{voiceName}\" \"{outputPath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(processStartInfo))
                {
                    if (process == null)
                        throw new Exception("Failed to start TTS python script.");

                    using (var writer = process.StandardInput)
                    {
                        await writer.WriteLineAsync(req.Text);
                    }

                    var stderr = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"TTS generation process exited with code {process.ExitCode}. Stderr: {stderr}");
                    }
                }
            }
            else
            {
                var baseDir = Path.Combine(AppContext.BaseDirectory, "Personality", "voices");
                if (!await DownloadVoiceIfNeeded(voiceName, baseDir))
                    return StatusCode(500, new { error = $"Failed to download or locate voice model for '{voiceName}'" });

                var modelPath = Path.Combine(baseDir, $"{voiceName}.onnx");
                var configPath = Path.Combine(baseDir, $"{voiceName}.onnx.json");
                var piperExe = @"C:\Users\admin\piper\piper\piper.exe";

                if (!System.IO.File.Exists(piperExe))
                    return StatusCode(500, new { error = "piper.exe was not found on the server." });

                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = piperExe,
                    Arguments = $"--model \"{modelPath}\" --config \"{configPath}\" --output_file \"{outputPath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(processStartInfo))
                {
                    if (process == null)
                        throw new Exception("Failed to start TTS piper process.");

                    using (var writer = process.StandardInput)
                    {
                        await writer.WriteLineAsync(req.Text);
                    }

                    var stderr = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"TTS generation process exited with code {process.ExitCode}. Stderr: {stderr}");
                    }
                }
            }

            if (!System.IO.File.Exists(outputPath))
                throw new Exception("TTS output file was not created.");

            var webUrl = $"/uploads/{companionSubdir}/{userSubdir}/{filename}".Replace('\\', '/');
            return Ok(new { url = webUrl });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static readonly HttpClient _httpClient = new();

    private static async Task<bool> DownloadVoiceIfNeeded(string voiceName, string modelsDir)
    {
        var modelPath = Path.Combine(modelsDir, $"{voiceName}.onnx");
        var configPath = Path.Combine(modelsDir, $"{voiceName}.onnx.json");

        if (System.IO.File.Exists(modelPath) && System.IO.File.Exists(configPath))
        {
            return true;
        }

        // Format example: en_US-amy-medium
        var parts = voiceName.Split('-');
        if (parts.Length < 3) return false;

        var langCode = parts[0]; // e.g. en_US
        var lang = langCode.Split('_')[0]; // e.g. en
        var speaker = parts[1]; // e.g. amy
        var quality = parts[2]; // e.g. medium

        var baseUrl = $"https://huggingface.co/rhasspy/piper-voices/resolve/main/{lang}/{langCode}/{speaker}/{quality}/{voiceName}";
        var onnxUrl = $"{baseUrl}.onnx";
        var jsonUrl = $"{baseUrl}.onnx.json";

        try
        {
            Directory.CreateDirectory(modelsDir);
            
            // Download JSON config first (usually smaller, fast check)
            if (!System.IO.File.Exists(configPath))
            {
                Console.WriteLine($"[tts] Downloading config from {jsonUrl}...");
                var jsonBytes = await _httpClient.GetByteArrayAsync(jsonUrl);
                await System.IO.File.WriteAllBytesAsync(configPath, jsonBytes);
            }

            // Download ONNX model
            if (!System.IO.File.Exists(modelPath))
            {
                Console.WriteLine($"[tts] Downloading voice model from {onnxUrl}...");
                var modelBytes = await _httpClient.GetByteArrayAsync(onnxUrl);
                await System.IO.File.WriteAllBytesAsync(modelPath, modelBytes);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tts] Failed to download voice '{voiceName}': {ex.Message}");
            // Clean up partial files if download failed
            try { if (System.IO.File.Exists(modelPath)) System.IO.File.Delete(modelPath); } catch {}
            try { if (System.IO.File.Exists(configPath)) System.IO.File.Delete(configPath); } catch {}
            return false;
        }
    }

    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50 MB
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] string? companion)
    {
        // Permission gate — admins always bypass
        if (!IsAdmin && !await _db.UserHasPermission(UserId, AshServer.Auth.Permissions.FileUpload))
            return StatusCode(403, new { error = "You do not have permission to upload files." });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });

        string companionSubdir = "_global";
        if (!string.IsNullOrWhiteSpace(companion))
        {
            companionSubdir = string.Concat(companion.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrWhiteSpace(companionSubdir))
            {
                companionSubdir = "_global";
            }
        }

        var userSubdir = $"user_{UserId}";
        var relativeSubpath = Path.Combine("uploads", companionSubdir, userSubdir);
        var targetDir = Path.Combine(_env.WebRootPath, relativeSubpath);
        
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var safeName = $"{Guid.NewGuid()}{ext}";
        var fullPath = Path.Combine(targetDir, safeName);

        using (var stream = System.IO.File.Create(fullPath))
            await file.CopyToAsync(stream);

        if (ext is ".glb" or ".vrm")
        {
            try
            {
                var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "convert_vrm_to_unlit.py");
                if (System.IO.File.Exists(scriptPath))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "py",
                        Arguments = $"-3 \"{scriptPath}\" \"{fullPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(10000);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[upload] Error converting VRM materials: {ex.Message}");
            }
        }

        var isImage = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp";
        var url = $"/uploads/{companionSubdir}/{userSubdir}/{safeName}".Replace('\\', '/');

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

            // Trigger background semantic indexing for RAG
            _ = Task.Run(async () => await _ragService.IndexDocumentAsync(file.FileName, textContent));
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
    private readonly AshServer.AI.GridManager _grid;
    private readonly HardwareProfiler _profiler;

    public AdminController(Database db, AshServer.AI.BackendManager backends, IConfiguration config,
        AshServer.Personality.PersonalityLoader personality, AshServer.Plugins.PluginManager plugins,
        ILogger<AdminController> log, IWebHostEnvironment env, UpdateManager updateManager,
        AshServer.AI.GridManager grid, HardwareProfiler profiler)
    {
        _db = db;
        _backends = backends;
        _config = config;
        _personality = personality;
        _plugins = plugins;
        _log = log;
        _env = env;
        _updateManager = updateManager;
        _grid = grid;
        _profiler = profiler;
    }

    private string ConfigPath => Path.Combine(_env.ContentRootPath, "config.json");

    private bool IsAdmin => User.FindFirstValue("is_admin") == "true";

    [HttpPost("organize-uploads")]
    public async Task<IActionResult> OrganizeUploads()
    {
        // Permission gate — admins only
        if (!IsAdmin) return Forbid();

        var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var uploadsDir = Path.Combine(webRoot, "uploads");

        if (!Directory.Exists(uploadsDir))
            return Ok(new { message = "Uploads directory does not exist. Nothing to organize." });

        // 1. Build a map of ConversationId -> CompanionName from all local companion JSON files
        var conversationToCompanionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var companionToAvatarMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
        var companionsBaseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
        var companionsLocalDir = Path.Combine(companionsBaseDir, "local");

        void ScanCompanionDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var content = System.IO.File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    
                    string? name = null;
                    if (root.TryGetProperty("name", out var nameProp) || root.TryGetProperty("Name", out nameProp))
                        name = nameProp.GetString();

                    if (string.IsNullOrEmpty(name)) continue;

                    string? conversationId = null;
                    if (root.TryGetProperty("conversationId", out var convProp))
                        conversationId = convProp.GetString();
                    else if (root.TryGetProperty("conversation_id", out var convProp2))
                        conversationId = convProp2.GetString();

                    if (!string.IsNullOrEmpty(conversationId))
                    {
                        conversationToCompanionMap[conversationId] = name;
                    }

                    string? avatarPath = null;
                    if (root.TryGetProperty("avatarPath", out var avatarProp))
                        avatarPath = avatarProp.GetString();
                    else if (root.TryGetProperty("avatar_path", out var avatarProp2))
                        avatarPath = avatarProp2.GetString();

                    if (!string.IsNullOrEmpty(avatarPath))
                    {
                        companionToAvatarMap[name] = avatarPath;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[organize] Failed to read companion file {file}: {ex.Message}");
                }
            }
        }

        ScanCompanionDir(companionsBaseDir);
        ScanCompanionDir(companionsLocalDir);

        // 2. Iterate over all files directly inside the uploads directory
        var filesMoved = 0;
        var referencesUpdated = 0;

        foreach (var filePath in Directory.GetFiles(uploadsDir))
        {
            var fileName = Path.GetFileName(filePath);
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            // Ignore temporary or system files
            if (fileName.StartsWith(".") || fileName.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                continue;

            string? conversationId = null;
            int? userId = null;
            string? companionName = null;

            // Search database for messages referencing this file
            var searchPattern = $"/uploads/{fileName}";
            using (var conn = _db.Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT conversation_id FROM messages WHERE content LIKE $pattern LIMIT 1";
                    cmd.Parameters.AddWithValue("$pattern", $"%{searchPattern}%");
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            conversationId = reader.GetString(0);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(conversationId))
                {
                    // Look up owner and companion_id in database
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT user_id, companion_id FROM conversations WHERE id = $id";
                        cmd.Parameters.AddWithValue("$id", conversationId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                userId = reader.GetInt32(0);
                                companionName = reader.IsDBNull(1) ? null : reader.GetString(1);
                            }
                        }
                    }
                }
            }

            // Resolve companion name using JSON overrides if database companion_id was null
            if (string.IsNullOrEmpty(companionName) && !string.IsNullOrEmpty(conversationId))
            {
                conversationToCompanionMap.TryGetValue(conversationId, out companionName);
            }

            // If we still don't have companion name, check if any companion's avatar matches this file
            if (string.IsNullOrEmpty(companionName))
            {
                foreach (var pair in companionToAvatarMap)
                {
                    if (pair.Value.Contains(fileName))
                    {
                        companionName = pair.Key;
                        break;
                    }
                }
            }

            // Fallback values if not associated with any conversation or companion
            var resolvedCompanion = string.IsNullOrEmpty(companionName) ? "_global" : string.Concat(companionName.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrEmpty(resolvedCompanion)) resolvedCompanion = "_global";

            var resolvedUserId = userId ?? int.Parse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);

            // Target subfolder structure: uploads/<companion>/user_<userId>/
            var userSubdir = $"user_{resolvedUserId}";
            var relativeSubpath = Path.Combine("uploads", resolvedCompanion, userSubdir);
            var targetDir = Path.Combine(webRoot, relativeSubpath);

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            var newFilePath = Path.Combine(targetDir, fileName);
            var newWebUrl = $"/uploads/{resolvedCompanion}/{userSubdir}/{fileName}".Replace('\\', '/');

            try
            {
                // Move file on disk
                if (System.IO.File.Exists(filePath))
                {
                    if (System.IO.File.Exists(newFilePath))
                    {
                        System.IO.File.Delete(newFilePath);
                    }
                    System.IO.File.Move(filePath, newFilePath);
                    filesMoved++;
                }

                // Update database references in messages table
                using (var conn = _db.Open())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE messages SET content = replace(content, $old, $new) WHERE content LIKE $old";
                        cmd.Parameters.AddWithValue("$old", searchPattern);
                        cmd.Parameters.AddWithValue("$new", newWebUrl);
                        var updated = cmd.ExecuteNonQuery();
                        referencesUpdated += updated;
                    }

                    // Update conversations.companion_id if it was null but we resolved the name
                    if (!string.IsNullOrEmpty(conversationId) && !string.IsNullOrEmpty(companionName))
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE conversations SET companion_id = $c WHERE id = $id AND (companion_id IS NULL OR companion_id = '')";
                            cmd.Parameters.AddWithValue("$id", conversationId);
                            cmd.Parameters.AddWithValue("$c", companionName);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                // Update companion JSON config files if they use this avatar path
                void UpdateCompanionJsonAvatar(string dir)
                {
                    if (!Directory.Exists(dir)) return;
                    foreach (var file in Directory.GetFiles(dir, "*.json"))
                    {
                        try
                        {
                            var fileContent = System.IO.File.ReadAllText(file);
                            if (fileContent.Contains(fileName))
                            {
                                var modified = fileContent
                                    .Replace($"/uploads/{fileName}", newWebUrl)
                                    .Replace(fileName, newWebUrl); // handle simple relative filenames
                                System.IO.File.WriteAllText(file, modified);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[organize] Failed to update avatar path in companion file {file}: {ex.Message}");
                        }
                    }
                }

                UpdateCompanionJsonAvatar(companionsBaseDir);
                UpdateCompanionJsonAvatar(companionsLocalDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[organize] Failed to move file {fileName}: {ex.Message}");
            }
        }

        return Ok(new
        {
            success = true,
            files_moved = filesMoved,
            database_references_updated = referencesUpdated,
            message = $"Successfully organized {filesMoved} legacy upload files into companion and user subfolders."
        });
    }

    [HttpGet("proactive")]
    public IActionResult GetProactive()
    {
        var disabled = _config.GetValue<bool>("ai:DisableProactive", false);
        return Ok(new { enabled = !disabled });
    }

    [HttpPost("proactive")]
    public async Task<IActionResult> SetProactive([FromBody] System.Collections.Generic.Dictionary<string, bool> body)
    {
        if (body == null || !body.TryGetValue("enabled", out var enabled))
            return BadRequest(new { error = "enabled is required" });

        var path = ConfigPath;
        if (!System.IO.File.Exists(path))
            await System.IO.File.WriteAllTextAsync(path, "{}");

        var raw = await System.IO.File.ReadAllTextAsync(path);
        var root = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
        
        if (!root.ContainsKey("ai"))
            root["ai"] = new System.Text.Json.Nodes.JsonObject();

        root["ai"]!.AsObject()["DisableProactive"] = !enabled;

        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        await System.IO.File.WriteAllTextAsync(path, root.ToJsonString(opts));

        return Ok(new { ok = true, enabled });
    }

    [HttpGet("tailscale")]
    public IActionResult GetTailscaleStatus()
    {
        if (!IsAdmin) return Forbid();
        
        var tailscaleOnly = _config.GetValue("TailscaleOnly", false);
        var tsIp = AshServer.Program.DiscoverTailscaleIp();
        
        return Ok(new
        {
            tailscale_only = tailscaleOnly,
            tailscale_ip = tsIp?.ToString()
        });
    }

    [HttpPost("tailscale")]
    public async Task<IActionResult> SaveTailscaleSettings([FromBody] Dictionary<string, bool> body)
    {
        if (!IsAdmin) return Forbid();
        if (!body.TryGetValue("tailscale_only", out var tailscaleOnly))
            return BadRequest(new { error = "tailscale_only field is required" });

        var path = ConfigPath;
        System.Text.Json.Nodes.JsonObject root;
        if (System.IO.File.Exists(path))
        {
            try { root = System.Text.Json.Nodes.JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path))!.AsObject(); }
            catch { root = new System.Text.Json.Nodes.JsonObject(); }
        }
        else
        {
            root = new System.Text.Json.Nodes.JsonObject();
        }

        root["TailscaleOnly"] = tailscaleOnly;

        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        await System.IO.File.WriteAllTextAsync(path, root.ToJsonString(opts));

        return Ok(new { ok = true, note = "Tailscale settings saved. Restart server to apply network binding changes." });
    }

    [HttpGet("grid/nodes")]
    public async Task<IActionResult> GetGridNodes()
    {
        if (!IsAdmin) return Forbid();

        var online = _grid.ActiveWorkers.Select(w => new
        {
            id = w.Id,
            name = w.Name,
            status = "online",
            hardware = w.Hardware,
            active_connections = w.ActiveConnections,
            last_heartbeat = w.LastHeartbeat
        }).ToList();

        var registered = await Task.Run(() =>
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, created_at FROM grid_workers ORDER BY name";
            using var r = cmd.ExecuteReader();
            var list = new List<object>();
            while (r.Read())
            {
                var rid = r.GetString(0);
                var isOnline = online.Any(o => o.id == rid);
                list.Add(new
                {
                    id = rid,
                    name = r.GetString(1),
                    status = isOnline ? "online" : "offline",
                    created_at = r.GetString(2)
                });
            }
            return list;
        });

        return Ok(new
        {
            online_count = online.Count,
            nodes = registered
        });
    }

    [HttpPost("grid/nodes/add")]
    public async Task<IActionResult> AddGridNode()
    {
        if (!IsAdmin) return Forbid();

        var token = await _grid.GeneratePairingTokenAsync();
        return Ok(new { token });
    }

    [HttpDelete("grid/nodes/{id}")]
    public async Task<IActionResult> DeleteGridNode(string id)
    {
        if (!IsAdmin) return Forbid();

        await Task.Run(() =>
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM grid_workers WHERE id = $i";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.ExecuteNonQuery();
        });

        return Ok(new { ok = true });
    }

    [HttpPost("grid/join")]
    public async Task<IActionResult> JoinGrid([FromBody] Dictionary<string, string> body)
    {
        if (!IsAdmin) return Forbid();

        if (!body.TryGetValue("master_url", out var masterUrl) || string.IsNullOrEmpty(masterUrl))
            return BadRequest(new { error = "master_url is required" });
        if (!body.TryGetValue("pairing_token", out var pairingToken) || string.IsNullOrEmpty(pairingToken))
            return BadRequest(new { error = "pairing_token is required" });

        var name = body.TryGetValue("name", out var n) ? n : Environment.MachineName;

        var path = ConfigPath;
        System.Text.Json.Nodes.JsonObject root;
        if (System.IO.File.Exists(path))
        {
            try { root = System.Text.Json.Nodes.JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path))!.AsObject(); }
            catch { root = new System.Text.Json.Nodes.JsonObject(); }
        }
        else
        {
            root = new System.Text.Json.Nodes.JsonObject();
        }

        root["Mode"] = "worker";
        if (!root.ContainsKey("Grid"))
        {
            root["Grid"] = new System.Text.Json.Nodes.JsonObject();
        }

        var gridNode = root["Grid"]!.AsObject();
        gridNode["MasterUrl"] = masterUrl;
        gridNode["PairingToken"] = pairingToken;
        gridNode["WorkerName"] = name;
        gridNode.Remove("WorkerId");
        gridNode.Remove("WorkerSecret");

        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        await System.IO.File.WriteAllTextAsync(path, root.ToJsonString(opts));

        return Ok(new { ok = true, note = "Configured in Worker Mode. Restarting background service to connect to Master..." });
    }

    private static async Task<bool> CheckPortHealthAsync(int port)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMilliseconds(300);
            var response = await client.GetAsync($"http://127.0.0.1:{port}/");
            return true;
        }
        catch
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connectTask = tcp.ConnectAsync("127.0.0.1", port);
                if (await Task.WhenAny(connectTask, Task.Delay(300)) == connectTask)
                {
                    await connectTask;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

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

        // Query real-time process resources
        var proc = Process.GetCurrentProcess();
        var ramUsageMb = Math.Round((double)proc.WorkingSet64 / (1024 * 1024), 2);
        var threadCount = proc.Threads.Count;
        var gcMemMb = Math.Round((double)GC.GetTotalMemory(false) / (1024 * 1024), 2);

        // Query sidecar health
        var llamaPort = 11436;
        var sdPort = 8080;
        var llamaHealth = "stopped";
        var sdHealth = "stopped";

        if (_profiler.IsLlamaRunning)
        {
            llamaHealth = await CheckPortHealthAsync(llamaPort) ? "ok" : "unreachable";
        }
        if (_profiler.IsSdRunning)
        {
            sdHealth = await CheckPortHealthAsync(sdPort) ? "ok" : "unreachable";
        }

        var systemProfile = _profiler.ProfileSystem();

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
                plugins_enabled  = _plugins.EnabledCount,
                ram_usage_mb     = ramUsageMb,
                threads_count    = threadCount,
                gc_memory_mb     = gcMemMb
            },
            system_profile = new
            {
                cpu_cores = systemProfile.CpuCores,
                total_ram_gb = Math.Round(systemProfile.TotalRamGb, 1),
                cuda_detected = systemProfile.HasCuda,
                optimal_threads = systemProfile.OptimalThreads,
                gpu_layers = systemProfile.GpuLayers
            },
            sidecars = new
            {
                llama = new
                {
                    running = _profiler.IsLlamaRunning,
                    pid = _profiler.LlamaPid,
                    port = llamaPort,
                    model = _profiler.LlamaModel,
                    context_size = _profiler.LlamaContextSize,
                    threads = _profiler.LlamaThreads,
                    health = llamaHealth,
                    cpu = _profiler.LlamaCpu,
                    ram_mb = _profiler.LlamaRamMb
                },
                stable_diffusion = new
                {
                    running = _profiler.IsSdRunning,
                    pid = _profiler.SdPid,
                    port = sdPort,
                    model = _profiler.SdModel,
                    steps = _profiler.SdSteps,
                    sampling_method = _profiler.SdSampling,
                    cfg_scale = _profiler.SdCfgScale,
                    health = sdHealth,
                    cpu = _profiler.SdCpu,
                    ram_mb = _profiler.SdRamMb
                }
            },
            diagnostics = new
            {
                logs = LogStore.GetLogs()
            }
        });
    }

    [HttpGet("devices")]
    public async Task<IActionResult> GetPairedDevices()
    {
        if (!IsAdmin) return Forbid();
        var devices = await _db.GetPairedDevices();
        return Ok(devices);
    }

    [HttpDelete("devices/{id}")]
    public async Task<IActionResult> DeletePairedDevice(int id)
    {
        if (!IsAdmin) return Forbid();
        await _db.DeletePairedDevice(id);
        return Ok(new { ok = true });
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
                ? new AshServer.AI.OpenAiCompatBackend(row.BaseUrl, row.ApiKey, _config)
                : new AshServer.AI.OllamaBackend(row.BaseUrl, _config);
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
        
        // Build a complete configuration representation by combining IConfiguration settings
        // (which naturally includes appsettings.json, environment variables, and config.json overlays)
        var mergedConfig = new
        {
            server = new
            {
                host = _config["Server:Host"] ?? _config["Host"] ?? "0.0.0.0",
                port = _config.GetValue("Server:Port", _config.GetValue("Port", 18799)),
                require_auth = _config.GetValue("Server:RequireAuth", _config.GetValue("RequireAuth", true)),
                allow_registration = _config.GetValue("Server:AllowRegistration", _config.GetValue("AllowRegistration", true))
            },
            ai = new
            {
                model = _config["Ai:Model"] ?? _config["DefaultModel"] ?? "",
                temperature = _config.GetValue("Ai:Temperature", _config.GetValue("DefaultTemperature", 0.7))
            },
            database = new
            {
                path = _config["Database:Path"] ?? _config["DatabasePath"] ?? "ash_server.db"
            },
            uploads = new
            {
                directory = _config["Uploads:Directory"] ?? _config["UploadsDir"] ?? "uploads",
                max_size_mb = _config.GetValue("Uploads:MaxSizeMb", _config.GetValue("MaxUploadSizeMb", 10))
            },
            auth = new
            {
                token_expiry_hours = _config.GetValue("Auth:TokenExpiryHours", _config.GetValue("TokenExpiryHours", 24))
            },
            personality = new
            {
                path = _config["Personality:Path"] ?? _config["PersonalityDir"] ?? "personality"
            }
        };

        return Ok(mergedConfig);
    }

    [HttpGet("network/interfaces")]
    public IActionResult GetNetworkInterfaces()
    {
        if (!IsAdmin) return Forbid();
        
        var list = new List<object>
        {
            new { name = "All Interfaces (Wildcard)", ip = "0.0.0.0", type = "Wildcard" },
            new { name = "Local Loopback (Private)", ip = "127.0.0.1", type = "Loopback" }
        };

        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) // IPv4
                    {
                        var ip = addr.Address.ToString();
                        if (ip == "127.0.0.1" || ip == "0.0.0.0") continue;
                        
                        var name = ni.Name;
                        var desc = ni.Description;
                        var type = ni.NetworkInterfaceType.ToString();
                        
                        // Detect any secure mesh VPN or virtual tunnels
                        if (IsVpnOrVirtual(ni))
                        {
                            type = "VPN";
                        }
                        
                        list.Add(new
                        {
                            name = $"{name} ({desc})",
                            ip = ip,
                            type = type
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to retrieve network interfaces");
        }

        return Ok(list);
    }

    private static bool IsVpnOrVirtual(System.Net.NetworkInformation.NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel ||
            ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ppp ||
            ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Slip)
        {
            return true;
        }

        var name = ni.Name.ToLowerInvariant();
        var desc = ni.Description.ToLowerInvariant();

        string[] vpnSignatures = new[] {
            "vpn", "wintun", "tun", "tap", "wireguard", "tailscale", "netbird", 
            "zerotier", "hamachi", "forticlient", "anyconnect", "checkpoint", 
            "openvpn", "nordvpn", "proton", "expressvpn", "mullvad", "softether",
            "virtual", "pseudo", "loopback", "bridge", "vmware", "virtualbox", 
            "hyper-v", "docker", "vethernet", "software", "ts0", "wt0", "wg0"
        };

        foreach (var sig in vpnSignatures)
        {
            if (name.Contains(sig) || desc.Contains(sig))
            {
                return true;
            }
        }

        try
        {
            var ipProps = ni.GetIPProperties();
            foreach (var addr in ipProps.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var ipBytes = addr.Address.GetAddressBytes();
                    if (ipBytes[0] == 100 && ipBytes[1] >= 64 && ipBytes[1] <= 127)
                    {
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
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
            var ollama = new AshServer.AI.OllamaBackend(ollamaUrl, _config);
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
            var ollama = new AshServer.AI.OllamaBackend("http://localhost:11434", _config);
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
            p.Config,
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

    [HttpPost("plugins/{id}/config")]
    public IActionResult SavePluginConfig(string id, [FromBody] Dictionary<string, JsonElement> newConfig)
    {
        if (!IsAdmin) return Forbid();
        var plugin = _plugins.Plugins.FirstOrDefault(p => p.Id == id);
        if (plugin == null) return NotFound(new { error = "Plugin not found" });
        if (plugin.Builtin) return BadRequest(new { error = "Built-in plugins cannot be configured" });

        try
        {
            var configPath = Path.Combine(plugin.DirectoryPath, "config.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            var jsonString = JsonSerializer.Serialize(newConfig, options);
            System.IO.File.WriteAllText(configPath, jsonString);

            plugin.Config = newConfig;
            return Ok(new { ok = true, id, config = newConfig });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to save plugin config: {ex.Message}" });
        }
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
        var fullPath = Path.GetFullPath(dbPath);
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "Database file not found" });
        
        var backupName = $"ash_server_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db";
        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var backupPath = Path.Combine(dir, backupName);
        System.IO.File.Copy(fullPath, backupPath);
        
        var size = new FileInfo(backupPath).Length;
        
        return Ok(new
        {
            ok = true,
            filename = backupName,
            size = size,
            message = "Database backup created successfully."
        });
    }

    [HttpPost("backup/haven")]
    public async Task<IActionResult> BackupHaven()
    {
        if (!IsAdmin) return Forbid();
        
        var dbPath = _config["DatabasePath"] ?? "ash_server.db";
        var fullPath = Path.GetFullPath(dbPath);
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "Database file not found" });

        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var backupName = $"haven_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.haven";
        var backupPath = Path.Combine(dir, backupName);

        try
        {
            using (var zipStream = new FileStream(backupPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                // 1. Add database
                var tempDb = Path.GetTempFileName();
                System.IO.File.Copy(fullPath, tempDb, true);
                archive.CreateEntryFromFile(tempDb, "db.sqlite");
                try { System.IO.File.Delete(tempDb); } catch {}

                // 2. Add uploads
                var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
                if (Directory.Exists(uploadsDir))
                {
                    foreach (var file in Directory.GetFiles(uploadsDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(uploadsDir, file);
                        archive.CreateEntryFromFile(file, Path.Combine("uploads", relativePath));
                    }
                }

                // 3. Add companions
                var personalityPath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
                var companionsDir = Path.Combine(AppContext.BaseDirectory, personalityPath, "companions");
                if (Directory.Exists(companionsDir))
                {
                    foreach (var file in Directory.GetFiles(companionsDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(companionsDir, file);
                        archive.CreateEntryFromFile(file, Path.Combine("companions", relativePath));
                    }
                }
            }

            var size = new FileInfo(backupPath).Length;
            return Ok(new
            {
                ok = true,
                filename = backupName,
                size = size,
                message = "Haven backup package created successfully."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to create Haven backup package: {ex.Message}" });
        }
    }

    [HttpPost("restore/haven")]
    public async Task<IActionResult> RestoreHaven([FromForm] IFormFile file)
    {
        if (!IsAdmin) return Forbid();
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        if (!file.FileName.EndsWith(".haven", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only .haven packages are supported." });

        var dbPath = _config["DatabasePath"] ?? "ash_server.db";
        var fullPath = Path.GetFullPath(dbPath);

        // Save uploaded package to temp folder
        var tempPackage = Path.GetTempFileName();
        using (var fs = new FileStream(tempPackage, FileMode.Create))
        {
            await file.CopyToAsync(fs);
        }

        try
        {
            // Verify it contains db.sqlite
            bool hasDb = false;
            using (var archive = ZipFile.OpenRead(tempPackage))
            {
                hasDb = archive.Entries.Any(e => e.FullName.Equals("db.sqlite", StringComparison.OrdinalIgnoreCase));
            }

            if (!hasDb)
            {
                return BadRequest(new { error = "Invalid .haven package: missing db.sqlite database." });
            }

            // Close active connections to clear the SQLite file lock
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Extract the database and folders
            using (var archive = ZipFile.OpenRead(tempPackage))
            {
                // Delete existing WAL files to avoid corruption
                var walPath = fullPath + "-wal";
                var shmPath = fullPath + "-shm";
                try { if (System.IO.File.Exists(walPath)) System.IO.File.Delete(walPath); } catch {}
                try { if (System.IO.File.Exists(shmPath)) System.IO.File.Delete(shmPath); } catch {}

                // Extract db
                var dbEntry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("db.sqlite", StringComparison.OrdinalIgnoreCase));
                if (dbEntry != null)
                {
                    dbEntry.ExtractToFile(fullPath, overwrite: true);
                }

                // Extract uploads
                var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
                Directory.CreateDirectory(uploadsDir);
                foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories
                    var relativePath = entry.FullName.Substring("uploads/".Length);
                    var targetPath = Path.Combine(uploadsDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    entry.ExtractToFile(targetPath, overwrite: true);
                }

                // Extract companions
                var personalityPath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
                var companionsDir = Path.Combine(AppContext.BaseDirectory, personalityPath, "companions");
                Directory.CreateDirectory(companionsDir);
                foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("companions/", StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories
                    var relativePath = entry.FullName.Substring("companions/".Length);
                    var targetPath = Path.Combine(companionsDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    entry.ExtractToFile(targetPath, overwrite: true);
                }
            }

            return Ok(new { ok = true, message = "System state successfully restored from .haven package." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to restore .haven package: {ex.Message}" });
        }
        finally
        {
            try { System.IO.File.Delete(tempPackage); } catch {}
        }
    }

    [HttpGet("database/status")]
    public IActionResult GetDatabaseStatus()
    {
        if (!IsAdmin) return Forbid();
        
        var dbPath = _config["DatabasePath"] ?? "ash_server.db";
        var fullPath = Path.GetFullPath(dbPath);
        
        long dbSize = 0;
        if (System.IO.File.Exists(fullPath))
        {
            dbSize = new FileInfo(fullPath).Length;
        }

        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var backupFiles = new List<object>();
        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(dir, "ash_server_backup_*.db")
                .Concat(Directory.GetFiles(dir, "haven_backup_*.haven"));
            foreach (var f in files)
            {
                var fi = new FileInfo(f);
                backupFiles.Add(new
                {
                    filename = fi.Name,
                    size = fi.Length,
                    created_at = fi.CreationTimeUtc.ToString("o")
                });
            }
        }

        return Ok(new
        {
            path = dbPath,
            size = dbSize,
            backups = backupFiles
        });
    }

    [HttpDelete("database/backups/{filename}")]
    public IActionResult DeleteBackup(string filename)
    {
        if (!IsAdmin) return Forbid();
        
        if (string.IsNullOrEmpty(filename) || filename.Contains("..") || filename.Contains("/") || filename.Contains("\\"))
        {
            return BadRequest(new { error = "Invalid filename" });
        }
        if (!filename.StartsWith("ash_server_backup_") && !filename.StartsWith("haven_backup_"))
        {
            return BadRequest(new { error = "Invalid backup file format" });
        }
        if (!filename.EndsWith(".db") && !filename.EndsWith(".haven"))
        {
            return BadRequest(new { error = "Invalid backup file format" });
        }

        var dbPath = _config["DatabasePath"] ?? "ash_server.db";
        var fullPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var targetFile = Path.Combine(dir, filename);

        if (!System.IO.File.Exists(targetFile))
        {
            return NotFound(new { error = "Backup file not found" });
        }

        try
        {
            System.IO.File.Delete(targetFile);
            return Ok(new { ok = true, message = "Backup deleted successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to delete backup: {ex.Message}" });
        }
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

    [HttpPost("sidecars/llama/stop")]
    public async Task<IActionResult> StopLlama()
    {
        if (!IsAdmin) return Forbid();
        await _profiler.StopLlamaAsync();
        return Ok(new { ok = true, message = "llama-server sidecar stopped." });
    }

    [HttpPost("sidecars/llama/start")]
    public async Task<IActionResult> StartLlama()
    {
        if (!IsAdmin) return Forbid();
        if (_profiler.IsLlamaRunning && _profiler.LlamaPid != null) return BadRequest(new { error = "llama-server is already running." });
        await _profiler.InitializeLocalBackendAsync();
        return Ok(new { ok = true, message = "llama-server sidecar started." });
    }

    [HttpPost("sidecars/llama/restart")]
    public async Task<IActionResult> RestartLlama()
    {
        if (!IsAdmin) return Forbid();
        await _profiler.StopLlamaAsync();
        await _profiler.InitializeLocalBackendAsync();
        return Ok(new { ok = true, message = "llama-server sidecar restarted." });
    }

    [HttpPost("sidecars/sd/stop")]
    public async Task<IActionResult> StopSd()
    {
        if (!IsAdmin) return Forbid();
        await _profiler.StopSdAsync();
        return Ok(new { ok = true, message = "sd-server sidecar stopped." });
    }

    [HttpPost("sidecars/sd/start")]
    public async Task<IActionResult> StartSd()
    {
        if (!IsAdmin) return Forbid();
        if (_profiler.IsSdRunning && _profiler.SdPid != null) return BadRequest(new { error = "sd-server is already running." });
        await _profiler.InitializeSdBackendAsync();
        return Ok(new { ok = true, message = "sd-server sidecar started." });
    }

    [HttpPost("sidecars/sd/restart")]
    public async Task<IActionResult> RestartSd()
    {
        if (!IsAdmin) return Forbid();
        await _profiler.StopSdAsync();
        await _profiler.InitializeSdBackendAsync();
        return Ok(new { ok = true, message = "sd-server sidecar restarted." });
    }

    [HttpPost("sidecars/config")]
    public async Task<IActionResult> SaveSidecarSettings([FromBody] System.Text.Json.Nodes.JsonObject body)
    {
        if (!IsAdmin) return Forbid();

        var path = ConfigPath;
        System.Text.Json.Nodes.JsonObject root;
        if (System.IO.File.Exists(path))
        {
            try { root = System.Text.Json.Nodes.JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path))!.AsObject(); }
            catch { root = new System.Text.Json.Nodes.JsonObject(); }
        }
        else
        {
            root = new System.Text.Json.Nodes.JsonObject();
        }

        // Create or get "sidecars" object
        System.Text.Json.Nodes.JsonObject sidecarsObj;
        if (root.ContainsKey("sidecars") && root["sidecars"] is System.Text.Json.Nodes.JsonObject obj)
        {
            sidecarsObj = obj;
        }
        else
        {
            sidecarsObj = new System.Text.Json.Nodes.JsonObject();
            root["sidecars"] = sidecarsObj;
        }

        // Save Llama configuration
        if (body.ContainsKey("llama") && body["llama"] is System.Text.Json.Nodes.JsonObject llamaBody)
        {
            System.Text.Json.Nodes.JsonObject llamaObj;
            if (sidecarsObj.ContainsKey("llama") && sidecarsObj["llama"] is System.Text.Json.Nodes.JsonObject lObj)
            {
                llamaObj = lObj;
            }
            else
            {
                llamaObj = new System.Text.Json.Nodes.JsonObject();
                sidecarsObj["llama"] = llamaObj;
            }

            if (llamaBody.ContainsKey("threads")) llamaObj["threads"] = llamaBody["threads"]?.GetValue<int>();
            if (llamaBody.ContainsKey("context_size")) llamaObj["context_size"] = llamaBody["context_size"]?.GetValue<int>();
            if (llamaBody.ContainsKey("gpu_layers")) llamaObj["gpu_layers"] = llamaBody["gpu_layers"]?.GetValue<int>();
        }

        // Save SD configuration
        if (body.ContainsKey("stable_diffusion") && body["stable_diffusion"] is System.Text.Json.Nodes.JsonObject sdBody)
        {
            System.Text.Json.Nodes.JsonObject sdObj;
            if (sidecarsObj.ContainsKey("stable_diffusion") && sidecarsObj["stable_diffusion"] is System.Text.Json.Nodes.JsonObject sObj)
            {
                sdObj = sObj;
            }
            else
            {
                sdObj = new System.Text.Json.Nodes.JsonObject();
                sidecarsObj["stable_diffusion"] = sdObj;
            }

            if (sdBody.ContainsKey("steps")) sdObj["steps"] = sdBody["steps"]?.GetValue<int>();
            if (sdBody.ContainsKey("sampling_method")) sdObj["sampling_method"] = sdBody["sampling_method"]?.GetValue<string>();
            if (sdBody.ContainsKey("cfg_scale")) sdObj["cfg_scale"] = sdBody["cfg_scale"]?.GetValue<double>();
        }

        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        await System.IO.File.WriteAllTextAsync(path, root.ToJsonString(opts));

        return Ok(new { ok = true, note = "Sidecar configurations saved successfully." });
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
    private readonly HardwareProfiler _profiler;

    public HealthController(Database db, BackendManager backends, IConfiguration config, HardwareProfiler profiler)
    {
        _db = db; _backends = backends; _config = config; _profiler = profiler;
    }

    private static async Task<bool> CheckPortHealthAsync(int port)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMilliseconds(300);
            var response = await client.GetAsync($"http://127.0.0.1:{port}/");
            return true;
        }
        catch
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connectTask = tcp.ConnectAsync("127.0.0.1", port);
                if (await Task.WhenAny(connectTask, Task.Delay(300)) == connectTask)
                {
                    await connectTask;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
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

        // Query sidecar health
        var llamaPort = 11436;
        var sdPort = 8080;
        var llamaHealth = "stopped";
        var sdHealth = "stopped";

        if (_profiler.IsLlamaRunning)
        {
            llamaHealth = await CheckPortHealthAsync(llamaPort) ? "ok" : "unreachable";
        }
        if (_profiler.IsSdRunning)
        {
            sdHealth = await CheckPortHealthAsync(sdPort) ? "ok" : "unreachable";
        }

        var systemProfile = _profiler.ProfileSystem();

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
            },
            system_profile = new
            {
                cpu_cores = systemProfile.CpuCores,
                total_ram_gb = Math.Round(systemProfile.TotalRamGb, 1),
                cuda_detected = systemProfile.HasCuda,
                optimal_threads = systemProfile.OptimalThreads,
                gpu_layers = systemProfile.GpuLayers
            },
            sidecars = new
            {
                llama = new
                {
                    running = _profiler.IsLlamaRunning,
                    pid = _profiler.LlamaPid,
                    port = llamaPort,
                    model = _profiler.LlamaModel,
                    context_size = _profiler.LlamaContextSize,
                    threads = _profiler.LlamaThreads,
                    gpu_layers = _profiler.LlamaGpuLayers,
                    health = llamaHealth,
                    cpu = _profiler.LlamaCpu,
                    ram_mb = _profiler.LlamaRamMb
                },
                stable_diffusion = new
                {
                    running = _profiler.IsSdRunning,
                    pid = _profiler.SdPid,
                    port = sdPort,
                    model = _profiler.SdModel,
                    steps = _profiler.SdSteps,
                    sampling_method = _profiler.SdSampling,
                    cfg_scale = _profiler.SdCfgScale,
                    health = sdHealth,
                    cpu = _profiler.SdCpu,
                    ram_mb = _profiler.SdRamMb
                }
            },
            diagnostics = new
            {
                logs = LogStore.GetLogs()
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

public class ChatRequest
{
    public string Prompt { get; set; } = string.Empty;
    public string? Image { get; set; }
    public bool Stream { get; set; }
    public string? Model { get; set; }
    public string? ConversationId { get; set; }
    public string? DisplayName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("companion_name")]
    public string? CompanionName { get; set; }
}

// ── Companions endpoint (read folder of companions) ─────────────────────────

[ApiController]
[Route("api/companions")]
[Authorize]
public class CompanionsController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly AshServer.Data.Database _db;
    private readonly BackendManager _backends;
    private readonly AshServer.Plugins.PluginManager _plugins;

    public CompanionsController(IConfiguration config, AshServer.Data.Database db, BackendManager backends, AshServer.Plugins.PluginManager plugins)
    {
        _config = config;
        _db = db;
        _backends = backends;
        _plugins = plugins;
    }

    private int UserId => int.Parse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> ListCompanions()
    {
        var relativePath = _config["personality:path"] ?? "personality";
        var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
        var localDir = Path.Combine(baseDir, "local");
        
        if (!Directory.Exists(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }

        var companionsMap = new Dictionary<string, CompanionConfig>(StringComparer.OrdinalIgnoreCase);

        // Helper to load files into CompanionConfig map
        void LoadDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var content = System.IO.File.ReadAllText(file);
                    var cfg = JsonSerializer.Deserialize<CompanionConfig>(content, new JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    });
                    if (cfg != null && !string.IsNullOrEmpty(cfg.Name))
                    {
                        companionsMap[cfg.Name] = cfg;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[companions] Failed to parse profile {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        LoadDir(baseDir);
        LoadDir(localDir);

        // Inject user-specific database properties: ConversationId
        var resultList = new List<CompanionConfig>();
        foreach (var pair in companionsMap)
        {
            var cfg = pair.Value;
            var conv = await _db.GetConversationByCompanion(UserId, cfg.Name);
            if (conv != null)
            {
                cfg.ConversationId = conv.Id;
            }
            resultList.Add(cfg);
        }

        return Ok(resultList);
    }

    [HttpPost("{name}/memories")]
    public async Task<IActionResult> AddCompanionMemory(string name, [FromBody] CompanionMemoryReq req)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(req.Fact))
            return BadRequest(new { error = "Memory fact is required." });

        var id = await _db.SaveCompanionMemory(UserId, name, req.Category ?? "personal_fact", req.Fact, req.Importance);
        return Ok(new { id, ok = true });
    }

    [HttpDelete("{name}/memories/{id}")]
    public async Task<IActionResult> DeleteCompanionMemory(string name, int id)
    {
        await _db.DeleteCompanionMemory(id, UserId);
        return Ok(new { ok = true });
    }

    [HttpPost]
    public async Task<IActionResult> SaveCompanion([FromBody] CompanionConfig req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Companion name is required." });

        var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
        var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
        var localDir = Path.Combine(baseDir, "local");
        
        if (!Directory.Exists(localDir))
        {
            Directory.CreateDirectory(localDir);
        }

        // Validate name to avoid path traversal
        var cleanName = string.Concat(req.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
            return BadRequest(new { error = "Invalid companion name." });

        var filePath = Path.Combine(localDir, $"{cleanName.ToLowerInvariant()}.json");
        var baseFilePath = Path.Combine(baseDir, $"{cleanName.ToLowerInvariant()}.json");
        var checkPath = System.IO.File.Exists(filePath) ? filePath : (System.IO.File.Exists(baseFilePath) ? baseFilePath : null);

        if (checkPath != null)
        {
            try
            {
                var oldConfigJson = System.IO.File.ReadAllText(checkPath);
                var oldConfig = JsonSerializer.Deserialize<CompanionConfig>(oldConfigJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                if (oldConfig != null && !string.IsNullOrWhiteSpace(oldConfig.BodyType) && !string.IsNullOrWhiteSpace(req.BodyType))
                {
                    var oldGender = oldConfig.BodyType.Trim().ToLowerInvariant();
                    var newGender = req.BodyType.Trim().ToLowerInvariant();
                    if (oldGender != newGender && (oldGender == "male" || oldGender == "female") && (newGender == "male" || newGender == "female"))
                    {
                        req.Description = SwapGenderPronouns(req.Description ?? "", oldGender, newGender);
                        req.Personality = SwapGenderPronouns(req.Personality ?? "", oldGender, newGender);
                        req.SystemPrompt = SwapGenderPronouns(req.SystemPrompt ?? "", oldGender, newGender);
                        req.FirstMessage = SwapGenderPronouns(req.FirstMessage ?? "", oldGender, newGender);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CompanionsController] Failed to auto-swap companion pronouns on save: {ex.Message}");
            }
        }
        
        try
        {
            var json = JsonSerializer.Serialize(req, new JsonSerializerOptions { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            System.IO.File.WriteAllText(filePath, json);

            if (!string.IsNullOrEmpty(req.ConversationId))
            {
                await _db.SetConversationCompanion(req.ConversationId, req.Name);
            }

            return Ok(new { ok = true, message = $"Companion '{req.Name}' saved locally successfully.", config = req });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to save companion: {ex.Message}" });
        }
    }

    [HttpDelete("{name}")]
    public IActionResult DeleteCompanion(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "Companion name is required." });

        var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
        var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
        var localDir = Path.Combine(baseDir, "local");

        // Validate name to avoid path traversal
        var cleanName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        var localFilePath = Path.Combine(localDir, $"{cleanName.ToLowerInvariant()}.json");
        var baseFilePath = Path.Combine(baseDir, $"{cleanName.ToLowerInvariant()}.json");

        try
        {
            bool deleted = false;
            if (System.IO.File.Exists(localFilePath))
            {
                System.IO.File.Delete(localFilePath);
                deleted = true;
            }
            if (System.IO.File.Exists(baseFilePath))
            {
                System.IO.File.Delete(baseFilePath);
                deleted = true;
            }

            if (!deleted)
                return NotFound(new { error = $"Companion '{name}' not found." });

            return Ok(new { ok = true, message = $"Companion '{name}' deleted successfully." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to delete companion: {ex.Message}" });
        }
    }

    [HttpGet("{name}/memories")]
    public async Task<IActionResult> GetCompanionMemories(string name)
    {
        return Ok(await _db.GetMemories(UserId, name));
    }

    [HttpGet("{name}/diaries")]
    public async Task<IActionResult> GetCompanionDiaries(string name)
    {
        return Ok(await _db.GetDiaries(UserId, name));
    }

    [HttpPost("import-card")]
    public async Task<IActionResult> ImportTavernCard(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "PNG file is required." });

        try
        {
            byte[] fileBytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                fileBytes = ms.ToArray();
            }

            var jsonMetadata = PngTavernCardParser.ExtractTavernMetadata(fileBytes);
            if (string.IsNullOrEmpty(jsonMetadata))
            {
                return BadRequest(new { error = "No Tavern card metadata found. Make sure the PNG is a valid Tavern character card." });
            }

            // Parse json
            using var doc = JsonDocument.Parse(jsonMetadata);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;

            string name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { error = "Invalid character data in card: name is empty." });
            }

            // Validate name to avoid path traversal
            var cleanName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
                return BadRequest(new { error = "Invalid companion name." });

            string desc = data.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
            string personality = data.TryGetProperty("personality", out var p) ? p.GetString() ?? "" : "";
            string scenario = data.TryGetProperty("scenario", out var sc) ? sc.GetString() ?? "" : "";
            string firstMsg = data.TryGetProperty("first_mes", out var fm) ? fm.GetString() ?? "" : "";
            string systemPrompt = data.TryGetProperty("system_prompt", out var sp) ? sp.GetString() ?? "" : "";

            int relationshipXp = 0;
            int messageCount = 0;
            string? currentOutfit = null;
            string? currentLocation = null;
            string? currentMood = null;
            string? clothingState = null;
            string? bodyType = null;
            string? bodyShape = null;

            if (root.TryGetProperty("haven_metadata", out var meta))
            {
                if (meta.TryGetProperty("relationshipXp", out var rxp)) relationshipXp = rxp.GetInt32();
                if (meta.TryGetProperty("messageCount", out var mc)) messageCount = mc.GetInt32();
                if (meta.TryGetProperty("currentOutfit", out var co)) currentOutfit = co.GetString();
                if (meta.TryGetProperty("currentLocation", out var cl)) currentLocation = cl.GetString();
                if (meta.TryGetProperty("currentMood", out var cm)) currentMood = cm.GetString();
                if (meta.TryGetProperty("clothingState", out var cs)) clothingState = cs.GetString();
                if (meta.TryGetProperty("bodyType", out var bt)) bodyType = bt.GetString();
                if (meta.TryGetProperty("bodyShape", out var bs)) bodyShape = bs.GetString();

                // Restore memories
                if (meta.TryGetProperty("memories", out var memories) && memories.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mem in memories.EnumerateArray())
                    {
                        var content = mem.GetProperty("content").GetString() ?? "";
                        var category = mem.GetProperty("category").GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(category))
                        {
                            await _db.SaveMemory(UserId, name, content, category);
                        }
                    }
                }

                // Restore diaries
                if (meta.TryGetProperty("diaries", out var diaries) && diaries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var diary in diaries.EnumerateArray())
                    {
                        var dateString = diary.GetProperty("dateString").GetString() ?? "";
                        var content = diary.GetProperty("content").GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(dateString) && !string.IsNullOrWhiteSpace(content))
                        {
                            await _db.SaveDiary(UserId, name, dateString, content);
                        }
                    }
                }
            }

            // Save avatar image to wwwroot/uploads
            var webRoot = _config["Uploads:Directory"] ?? _config["UploadsDir"] ?? "wwwroot";
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, webRoot, "uploads");
            Directory.CreateDirectory(uploadsDir);
            var avatarFilename = $"companion_{cleanName.ToLowerInvariant()}.png";
            var avatarPath = Path.Combine(uploadsDir, avatarFilename);
            await System.IO.File.WriteAllBytesAsync(avatarPath, fileBytes);

            // Construct CompanionConfig
            var config = new CompanionConfig
            {
                Name = name,
                VoiceId = "en_US-amy-medium", // Default voice
                Description = desc,
                Personality = personality,
                Scenario = scenario,
                FirstMessage = firstMsg,
                SystemPrompt = systemPrompt,
                AvatarPath = $"/uploads/{avatarFilename}",
                RelationshipXp = relationshipXp,
                MessageCount = messageCount,
                CurrentOutfit = currentOutfit,
                CurrentLocation = currentLocation,
                CurrentMood = currentMood,
                ClothingState = clothingState,
                BodyType = bodyType,
                BodyShape = bodyShape
            };

            var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
            var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
            Directory.CreateDirectory(baseDir);

            var profilePath = Path.Combine(baseDir, $"{cleanName.ToLowerInvariant()}.json");
            var profileJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await System.IO.File.WriteAllTextAsync(profilePath, profileJson);

            return Ok(new { ok = true, character = config });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Import failed: {ex.Message}" });
        }
    }

    [HttpPost("import-url")]
    public async Task<IActionResult> ImportTavernCardFromUrl([FromBody] ImportUrlRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "PNG Card URL is required." });

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            
            var response = await client.GetAsync(req.Url);
            if (!response.IsSuccessStatusCode)
                return BadRequest(new { error = $"Failed to download file from URL. HTTP Status: {response.StatusCode}" });

            var fileBytes = await response.Content.ReadAsByteArrayAsync();

            var jsonMetadata = PngTavernCardParser.ExtractTavernMetadata(fileBytes);
            if (string.IsNullOrEmpty(jsonMetadata))
            {
                return BadRequest(new { error = "No Tavern card metadata found in the downloaded file. Make sure the link points directly to a valid PNG card." });
            }

            // Parse json
            using var doc = JsonDocument.Parse(jsonMetadata);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;

            string name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { error = "Invalid character data in card: name is empty." });
            }

            // Validate name to avoid path traversal
            var cleanName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
                return BadRequest(new { error = "Invalid companion name." });

            string desc = data.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
            string personality = data.TryGetProperty("personality", out var p) ? p.GetString() ?? "" : "";
            string scenario = data.TryGetProperty("scenario", out var sc) ? sc.GetString() ?? "" : "";
            string firstMsg = data.TryGetProperty("first_mes", out var fm) ? fm.GetString() ?? "" : "";
            string systemPrompt = data.TryGetProperty("system_prompt", out var sp) ? sp.GetString() ?? "" : "";

            int relationshipXp = 0;
            int messageCount = 0;
            string? currentOutfit = null;
            string? currentLocation = null;
            string? currentMood = null;
            string? clothingState = null;
            string? bodyType = null;
            string? bodyShape = null;

            if (root.TryGetProperty("haven_metadata", out var meta))
            {
                if (meta.TryGetProperty("relationshipXp", out var rxp)) relationshipXp = rxp.GetInt32();
                if (meta.TryGetProperty("messageCount", out var mc)) messageCount = mc.GetInt32();
                if (meta.TryGetProperty("currentOutfit", out var co)) currentOutfit = co.GetString();
                if (meta.TryGetProperty("currentLocation", out var cl)) currentLocation = cl.GetString();
                if (meta.TryGetProperty("currentMood", out var cm)) currentMood = cm.GetString();
                if (meta.TryGetProperty("clothingState", out var cs)) clothingState = cs.GetString();
                if (meta.TryGetProperty("bodyType", out var bt)) bodyType = bt.GetString();
                if (meta.TryGetProperty("bodyShape", out var bs)) bodyShape = bs.GetString();

                // Restore memories
                if (meta.TryGetProperty("memories", out var memories) && memories.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mem in memories.EnumerateArray())
                    {
                        var content = mem.GetProperty("content").GetString() ?? "";
                        var category = mem.GetProperty("category").GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(category))
                        {
                            await _db.SaveMemory(UserId, name, content, category);
                        }
                    }
                }

                // Restore diaries
                if (meta.TryGetProperty("diaries", out var diaries) && diaries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var diary in diaries.EnumerateArray())
                    {
                        var dateString = diary.GetProperty("dateString").GetString() ?? "";
                        var content = diary.GetProperty("content").GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(dateString) && !string.IsNullOrWhiteSpace(content))
                        {
                            await _db.SaveDiary(UserId, name, dateString, content);
                        }
                    }
                }
            }

            // Save avatar image to wwwroot/uploads
            var webRoot = _config["Uploads:Directory"] ?? _config["UploadsDir"] ?? "wwwroot";
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, webRoot, "uploads");
            Directory.CreateDirectory(uploadsDir);
            var avatarFilename = $"companion_{cleanName.ToLowerInvariant()}.png";
            var avatarPath = Path.Combine(uploadsDir, avatarFilename);
            await System.IO.File.WriteAllBytesAsync(avatarPath, fileBytes);

            // Construct CompanionConfig
            var config = new CompanionConfig
            {
                Name = name,
                VoiceId = "en_US-amy-medium", // Default voice
                Description = desc,
                Personality = personality,
                Scenario = scenario,
                FirstMessage = firstMsg,
                SystemPrompt = systemPrompt,
                AvatarPath = $"/uploads/{avatarFilename}",
                RelationshipXp = relationshipXp,
                MessageCount = messageCount,
                CurrentOutfit = currentOutfit,
                CurrentLocation = currentLocation,
                CurrentMood = currentMood,
                ClothingState = clothingState,
                BodyType = bodyType,
                BodyShape = bodyShape
            };

            var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
            var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
            Directory.CreateDirectory(baseDir);

            var profilePath = Path.Combine(baseDir, $"{cleanName.ToLowerInvariant()}.json");
            var profileJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await System.IO.File.WriteAllTextAsync(profilePath, profileJson);

            return Ok(new { ok = true, character = config });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Import from URL failed: {ex.Message}" });
        }
    }

    private static class PngTavernCardParser
    {
        public static string? ExtractTavernMetadata(byte[] pngBytes)
        {
            try
            {
                if (pngBytes.Length < 8 || 
                    pngBytes[0] != 0x89 || pngBytes[1] != 0x50 || 
                    pngBytes[2] != 0x4E || pngBytes[3] != 0x47 ||
                    pngBytes[4] != 0x0D || pngBytes[5] != 0x0A || 
                    pngBytes[6] != 0x1A || pngBytes[7] != 0x0A)
                {
                    return null;
                }

                int offset = 8;
                while (offset + 12 <= pngBytes.Length)
                {
                    uint length = ((uint)pngBytes[offset] << 24) |
                                  ((uint)pngBytes[offset + 1] << 16) |
                                  ((uint)pngBytes[offset + 2] << 8) |
                                  (uint)pngBytes[offset + 3];
                    
                    string type = Encoding.ASCII.GetString(pngBytes, offset + 4, 4);
                    
                    if (offset + 12 + length > pngBytes.Length)
                        break;
                    
                    if (type == "tEXt")
                    {
                        int keyEnd = offset + 8;
                        while (keyEnd < offset + 8 + length && pngBytes[keyEnd] != 0)
                        {
                            keyEnd++;
                        }
                        string keyword = Encoding.ASCII.GetString(pngBytes, offset + 8, keyEnd - (offset + 8));
                        if (keyword == "chara" || keyword == "ccv3")
                        {
                            int textStart = keyEnd + 1;
                            int textLength = (int)(offset + 8 + length - textStart);
                            if (textLength > 0)
                            {
                                var rawStr = Encoding.UTF8.GetString(pngBytes, textStart, textLength).Trim();
                                try 
                                { 
                                    return Encoding.UTF8.GetString(Convert.FromBase64String(rawStr)); 
                                } 
                                catch 
                                { 
                                    return rawStr; 
                                }
                            }
                        }
                    }
                    else if (type == "iTXt")
                    {
                        int keyEnd = offset + 8;
                        while (keyEnd < offset + 8 + length && pngBytes[keyEnd] != 0)
                        {
                            keyEnd++;
                        }
                        string keyword = Encoding.UTF8.GetString(pngBytes, offset + 8, keyEnd - (offset + 8));
                        if (keyword == "chara" || keyword == "ccv3")
                        {
                            int dataIdx = keyEnd + 1;
                            bool isCompressed = pngBytes[dataIdx] != 0;
                            dataIdx += 2;
                            
                            while (dataIdx < offset + 8 + length && pngBytes[dataIdx] != 0) dataIdx++;
                            dataIdx++;
                            
                            while (dataIdx < offset + 8 + length && pngBytes[dataIdx] != 0) dataIdx++;
                            dataIdx++;
                            
                            int textLength = (int)(offset + 8 + length - dataIdx);
                            if (textLength > 0)
                            {
                                byte[] textBytes = new byte[textLength];
                                Array.Copy(pngBytes, dataIdx, textBytes, 0, textLength);
                                
                                string decoded;
                                if (isCompressed)
                                {
                                    using var ms = new MemoryStream(textBytes, 2, textBytes.Length - 2); // skip zlib headers
                                    using var def = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                                    using var outMs = new MemoryStream();
                                    def.CopyTo(outMs);
                                    decoded = Encoding.UTF8.GetString(outMs.ToArray()).Trim();
                                }
                                else
                                {
                                    decoded = Encoding.UTF8.GetString(textBytes).Trim();
                                }
                                
                                try 
                                { 
                                    return Encoding.UTF8.GetString(Convert.FromBase64String(decoded)); 
                                } 
                                catch 
                                { 
                                    return decoded; 
                                }
                            }
                        }
                    }
                    
                    offset += 12 + (int)length;
                }
            }
            catch {}
            return null;
        }
    }

    [HttpPost("generate-profile")]
    public async Task<IActionResult> GenerateProfile([FromBody] GenerateProfileRequest req, CancellationToken cancellationToken)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "Prompt is required." });

        try
        {
            var systemPrompt = "You are a helpful assistant. Generate a character profile based on the user's idea.\n" +
                               "Output the profile STRICTLY in the following JSON format:\n" +
                               "{\n" +
                               "  \"name\": \"Character Name\",\n" +
                               "  \"description\": \"Short summary of background, appearance, and attire.\",\n" +
                               "  \"personality\": \"Detailed personality traits, habits, and speech mannerisms.\",\n" +
                               "  \"scenario\": \"Current roleplay setting or circumstances.\",\n" +
                               "  \"firstMessage\": \"The initial welcoming message the companion says to start the chat.\",\n" +
                               "  \"systemPrompt\": \"System directives for how this companion should act.\"\n" +
                               "}\n" +
                               "Do not write any extra conversational text, notes, or markdown codeblocks around the JSON. Output only the raw JSON.";

            var modelId = _config["DefaultModel"] ?? "";
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", $"Generate a companion for: {req.Prompt}")
            };

            var fullText = new System.Text.StringBuilder();
            await foreach (var token in _backends.StreamChat(modelId, messages, cancellationToken))
            {
                fullText.Append(token);
            }

            var jsonResult = fullText.ToString().Trim();

            // Extract JSON substring by finding the outermost '{' and '}' to bypass thought blocks or conversational wrappers
            var firstBrace = jsonResult.IndexOf('{');
            var lastBrace = jsonResult.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                jsonResult = jsonResult.Substring(firstBrace, lastBrace - firstBrace + 1);
            }
            else if (jsonResult.StartsWith("```"))
            {
                jsonResult = System.Text.RegularExpressions.Regex.Replace(jsonResult, @"^```[a-zA-Z]*\s*", "");
                jsonResult = System.Text.RegularExpressions.Regex.Replace(jsonResult, @"\s*```$", "");
                jsonResult = jsonResult.Trim();
            }

            using var doc = JsonDocument.Parse(jsonResult);
            return Ok(new { ok = true, profile = doc.RootElement });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Generation failed: {ex.Message}" });
        }
    }

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateCompanion([FromBody] GenerateCompanionRequest req, CancellationToken cancellationToken)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "Prompt is required." });

        try
        {
            var systemPrompt = "You are a helpful assistant. Generate a character profile based on the user's idea.\n" +
                               "Output the profile STRICTLY in the following JSON format:\n" +
                               "{\n" +
                               "  \"name\": \"Character Name\",\n" +
                               "  \"description\": \"Short summary of background, appearance, and attire.\",\n" +
                               "  \"personality\": \"Detailed personality traits, habits, and speech mannerisms.\",\n" +
                               "  \"scenario\": \"Current roleplay setting or circumstances.\",\n" +
                               "  \"firstMessage\": \"The initial welcoming message the companion says to start the chat.\",\n" +
                               "  \"systemPrompt\": \"System directives for how this companion should act.\",\n" +
                               "  \"bodyType\": \"Optional physical body characteristics, e.g. curvy, futanari, athletic.\",\n" +
                               "  \"bodyShape\": \"Optional details about shape, e.g. hourglass, futanari, muscular.\",\n" +
                               "  \"clothingState\": \"Active starting clothing state, e.g. fully clothed, naked, underwear.\"\n" +
                               "}\n" +
                               "Do not write any extra conversational text, notes, or markdown codeblocks around the JSON. Output only the raw JSON.";

            var modelId = _config["DefaultModel"] ?? "";
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", $"Generate a companion for: {req.Prompt}")
            };

            var fullText = new System.Text.StringBuilder();
            await foreach (var token in _backends.StreamChat(modelId, messages, cancellationToken))
            {
                fullText.Append(token);
            }

            var jsonResult = fullText.ToString().Trim();

            // Extract JSON substring by finding the outermost '{' and '}' to bypass thought blocks or conversational wrappers
            var firstBrace = jsonResult.IndexOf('{');
            var lastBrace = jsonResult.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                jsonResult = jsonResult.Substring(firstBrace, lastBrace - firstBrace + 1);
            }
            else if (jsonResult.StartsWith("```"))
            {
                jsonResult = System.Text.RegularExpressions.Regex.Replace(jsonResult, @"^```[a-zA-Z]*\s*", "");
                jsonResult = System.Text.RegularExpressions.Regex.Replace(jsonResult, @"\s*```$", "");
                jsonResult = jsonResult.Trim();
            }

            using var doc = JsonDocument.Parse(jsonResult);
            var root = doc.RootElement;

            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string desc = root.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
            string personality = root.TryGetProperty("personality", out var p) ? p.GetString() ?? "" : "";
            string scenario = root.TryGetProperty("scenario", out var sc) ? sc.GetString() ?? "" : "";
            string firstMsg = root.TryGetProperty("firstMessage", out var fm) ? fm.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(firstMsg))
            {
                firstMsg = root.TryGetProperty("first_mes", out var fm2) ? fm2.GetString() ?? "" : "";
            }
            string systemPromptStr = root.TryGetProperty("systemPrompt", out var sp) ? sp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(systemPromptStr))
            {
                systemPromptStr = root.TryGetProperty("system_prompt", out var sp2) ? sp2.GetString() ?? "" : "";
            }

            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { error = "LLM failed to generate a valid name." });

            var cleanName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
                cleanName = "companion_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Formulate Stable Diffusion prompt
            var sdPromptToUse = string.IsNullOrWhiteSpace(req.SdPrompt)
                ? $"digital art portrait of {name}, highly detailed, {desc}"
                : req.SdPrompt;

            var relativeImagePath = "";
            try
            {
                var sdArgObj = new { description = sdPromptToUse };
                var sdArgElement = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(sdArgObj));
                relativeImagePath = await _plugins.ExecuteTool("generate_portrait", sdArgElement);
                relativeImagePath = relativeImagePath.Trim();
                if (!relativeImagePath.StartsWith("/uploads/"))
                {
                    relativeImagePath = "";
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CompanionsController] SD generation failed: {ex.Message}");
            }

            string bodyType = root.TryGetProperty("bodyType", out var bt) ? bt.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(bodyType))
                bodyType = root.TryGetProperty("body_type", out var bt2) ? bt2.GetString() ?? "" : "";

            string bodyShape = root.TryGetProperty("bodyShape", out var bs) ? bs.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(bodyShape))
                bodyShape = root.TryGetProperty("body_shape", out var bs2) ? bs2.GetString() ?? "" : "";

            string clothingState = root.TryGetProperty("clothingState", out var cs) ? cs.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(clothingState))
                clothingState = root.TryGetProperty("clothing_state", out var cs2) ? cs2.GetString() ?? "" : "";

            // Fallback: If prompt explicitly requested futanari/futa or custom anatomical properties, force-match them into config fields
            if (req.Prompt.Contains("futa", StringComparison.OrdinalIgnoreCase) || req.Prompt.Contains("futanari", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(bodyType)) bodyType = "futanari";
                if (string.IsNullOrWhiteSpace(bodyShape)) bodyShape = "futanari";
            }

            var config = new CompanionConfig
            {
                Name = name,
                VoiceId = string.IsNullOrWhiteSpace(req.VoiceId) ? "en_US-amy-medium" : req.VoiceId,
                Description = desc,
                Personality = personality,
                Scenario = scenario,
                FirstMessage = firstMsg,
                SystemPrompt = systemPromptStr,
                AvatarPath = relativeImagePath,
                BodyType = bodyType,
                BodyShape = bodyShape,
                ClothingState = !string.IsNullOrWhiteSpace(clothingState) ? clothingState : "fully clothed"
            };

            var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
            var baseDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
            Directory.CreateDirectory(baseDir);

            var profilePath = Path.Combine(baseDir, $"{cleanName.ToLowerInvariant()}.json");
            var profileJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await System.IO.File.WriteAllTextAsync(profilePath, profileJson);

            return Ok(new { ok = true, character = config });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Generation failed: {ex.Message}" });
        }
    }

    [HttpGet("{name}/assets")]
    public async Task<IActionResult> GetCompanionAssets(string name)
    {
        var cleanName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        var webRoot = _config["Uploads:Directory"] ?? _config["UploadsDir"] ?? "wwwroot";
        var companionUploadDir = Path.Combine(AppContext.BaseDirectory, webRoot, "uploads", cleanName, $"user_{UserId}");

        var outfits = new List<object>();
        var locations = new List<object>();
        var moods = new List<object>();

        if (Directory.Exists(companionUploadDir))
        {
            var files = Directory.GetFiles(companionUploadDir);
            foreach (var file in files)
            {
                var filename = Path.GetFileName(file);
                var relativePath = $"/uploads/{cleanName}/user_{UserId}/{filename}".Replace('\\', '/');

                if (filename.StartsWith("outfit_", StringComparison.OrdinalIgnoreCase))
                {
                    outfits.Add(new { name = filename.Substring(7).Replace(".webp", ""), url = relativePath });
                }
                else if (filename.StartsWith("location_", StringComparison.OrdinalIgnoreCase))
                {
                    locations.Add(new { name = filename.Substring(9).Replace(".webp", ""), url = relativePath });
                }
                else if (filename.StartsWith("mood_", StringComparison.OrdinalIgnoreCase))
                {
                    moods.Add(new { name = filename.Substring(5).Replace(".webp", ""), url = relativePath });
                }
            }
        }

        return Ok(new { ok = true, outfits, locations, moods });
    }

    [HttpPost("{name}/generate-asset")]
    public async Task<IActionResult> GenerateCompanionAsset(string name, [FromBody] GenerateAssetRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.AssetType) || string.IsNullOrWhiteSpace(req.Value))
            return BadRequest(new { error = "AssetType and Value are required." });

        var cleanName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
        var profilePath = Path.Combine(AppContext.BaseDirectory, relativePath, "companions", $"{cleanName.ToLowerInvariant()}.json");

        if (!System.IO.File.Exists(profilePath))
            return NotFound(new { error = "Companion not found." });

        var configJson = await System.IO.File.ReadAllTextAsync(profilePath);
        var config = JsonSerializer.Deserialize<CompanionConfig>(configJson);
        if (config == null) return BadRequest(new { error = "Failed to parse companion config." });

        var assetType = req.AssetType.ToLowerInvariant();
        var value = req.Value.Trim();
        var targetFilename = $"{cleanName}/user_{UserId}/{assetType}_{value.ToLowerInvariant()}.webp";

        string sdPromptToUse;
        var btPart = !string.IsNullOrWhiteSpace(config.BodyType) ? $", body type: {config.BodyType}" : "";
        var bsPart = !string.IsNullOrWhiteSpace(config.BodyShape) ? $", body shape: {config.BodyShape}" : "";

        if (assetType == "outfit")
        {
            sdPromptToUse = string.IsNullOrWhiteSpace(req.SdPromptOverride)
                ? $"digital art portrait of {config.Name} wearing {value}{btPart}{bsPart}, highly detailed, {config.Description}"
                : req.SdPromptOverride;
        }
        else if (assetType == "location")
        {
            sdPromptToUse = string.IsNullOrWhiteSpace(req.SdPromptOverride)
                ? $"digital art landscape of {value}, highly detailed anime background scenery, dynamic lighting"
                : req.SdPromptOverride;
        }
        else if (assetType == "mood")
        {
            sdPromptToUse = string.IsNullOrWhiteSpace(req.SdPromptOverride)
                ? $"digital art portrait of {config.Name}, {value} facial expression{btPart}{bsPart}, highly detailed, {config.Description}"
                : req.SdPromptOverride;
        }
        else
        {
            return BadRequest(new { error = "Invalid AssetType. Must be outfit, location, or mood." });
        }

        var relativeImagePath = "";
        try
        {
            var sdArgObj = new { description = sdPromptToUse, target_filename = targetFilename };
            var sdArgElement = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(sdArgObj));
            relativeImagePath = await _plugins.ExecuteTool("generate_portrait", sdArgElement);
            relativeImagePath = relativeImagePath.Trim();
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Stable Diffusion generation failed: {ex.Message}" });
        }

        if (string.IsNullOrEmpty(relativeImagePath) || !relativeImagePath.StartsWith("/uploads/"))
        {
            return StatusCode(500, new { error = "Stable Diffusion failed to generate a valid image file." });
        }

        if (assetType == "outfit")
        {
            config.CurrentOutfit = value;
            config.AvatarPath = relativeImagePath;
        }
        else if (assetType == "location")
        {
            config.CurrentLocation = value;
        }
        else if (assetType == "mood")
        {
            config.CurrentMood = value;
            config.AvatarPath = relativeImagePath;
        }

        var updatedProfileJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await System.IO.File.WriteAllTextAsync(profilePath, updatedProfileJson);

        return Ok(new { ok = true, url = relativeImagePath, config });
    }

    [HttpPost("{name}/select-asset")]
    public async Task<IActionResult> SelectCompanionAsset(string name, [FromBody] SelectAssetRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.AssetType) || string.IsNullOrWhiteSpace(req.Value))
            return BadRequest(new { error = "AssetType and Value are required." });

        var cleanName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        var relativePath = _config["PersonalityDir"] ?? _config["personality:path"] ?? "personality";
        var profilePath = Path.Combine(AppContext.BaseDirectory, relativePath, "companions", $"{cleanName.ToLowerInvariant()}.json");

        if (!System.IO.File.Exists(profilePath))
            return NotFound(new { error = "Companion not found." });

        var configJson = await System.IO.File.ReadAllTextAsync(profilePath);
        var config = JsonSerializer.Deserialize<CompanionConfig>(configJson);
        if (config == null) return BadRequest(new { error = "Failed to parse companion config." });

        var assetType = req.AssetType.ToLowerInvariant();
        var value = req.Value.Trim();
        var expectedFilename = $"{assetType}_{value.ToLowerInvariant()}.webp";
        var webRoot = _config["Uploads:Directory"] ?? _config["UploadsDir"] ?? "wwwroot";
        var fileOnDisk = Path.Combine(AppContext.BaseDirectory, webRoot, "uploads", cleanName, $"user_{UserId}", expectedFilename);

        if (!System.IO.File.Exists(fileOnDisk))
        {
            if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                if (assetType == "outfit")
                {
                    config.CurrentOutfit = "default";
                    config.AvatarPath = $"/uploads/companion_{cleanName.ToLowerInvariant()}.png";
                }
                else if (assetType == "location")
                {
                    config.CurrentLocation = "default";
                }
                else if (assetType == "mood")
                {
                    config.CurrentMood = "default";
                    config.AvatarPath = $"/uploads/companion_{cleanName.ToLowerInvariant()}.png";
                }
            }
            else
            {
                return BadRequest(new { error = $"Asset file not found. Generate it first." });
            }
        }
        else
        {
            var relativeImagePath = $"/uploads/{cleanName}/user_{UserId}/{expectedFilename}".Replace('\\', '/');
            if (assetType == "outfit")
            {
                config.CurrentOutfit = value;
                config.AvatarPath = relativeImagePath;
            }
            else if (assetType == "location")
            {
                config.CurrentLocation = value;
            }
            else if (assetType == "mood")
            {
                config.CurrentMood = value;
                config.AvatarPath = relativeImagePath;
            }
        }

        var updatedProfileJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await System.IO.File.WriteAllTextAsync(profilePath, updatedProfileJson);

        return Ok(new { ok = true, config });
    }

    private static string SwapGenderPronouns(string text, string fromGender, string toGender)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var cleanFrom = fromGender.Trim().ToLowerInvariant();
        var cleanTo = toGender.Trim().ToLowerInvariant();

        if (cleanFrom == cleanTo) return text;

        var replacements = new List<(string pattern, string replacement)>();

        if (cleanFrom == "male" && cleanTo == "female")
        {
            replacements.Add((@"\bhe\b", "she"));
            replacements.Add((@"\bhim\b", "her"));
            replacements.Add((@"\bhis\b", "her"));
            replacements.Add((@"\bhimself\b", "herself"));
            replacements.Add((@"\bmale\b", "female"));
            replacements.Add((@"\bman\b", "woman"));
            replacements.Add((@"\bmen\b", "women"));
            replacements.Add((@"\bboy\b", "girl"));
            replacements.Add((@"\bboys\b", "girls"));
            replacements.Add((@"\bhusband\b", "wife"));
            replacements.Add((@"\bboyfriend\b", "girlfriend"));
            replacements.Add((@"\bson\b", "daughter"));
            replacements.Add((@"\bbrother\b", "sister"));
            replacements.Add((@"\bfather\b", "mother"));
            replacements.Add((@"\bgentleman\b", "lady"));
            replacements.Add((@"\bking\b", "queen"));
            replacements.Add((@"\bprince\b", "princess"));
        }
        else if (cleanFrom == "female" && cleanTo == "male")
        {
            replacements.Add((@"\bshe\b", "he"));
            replacements.Add((@"\bherself\b", "himself"));
            replacements.Add((@"\bhers\b", "his"));
            replacements.Add((@"\bher\b", "his"));
            replacements.Add((@"\bfemale\b", "male"));
            replacements.Add((@"\bwoman\b", "man"));
            replacements.Add((@"\bwomen\b", "men"));
            replacements.Add((@"\bgirl\b", "boy"));
            replacements.Add((@"\bgirls\b", "boys"));
            replacements.Add((@"\bwife\b", "husband"));
            replacements.Add((@"\bgirlfriend\b", "boyfriend"));
            replacements.Add((@"\bdaughter\b", "son"));
            replacements.Add((@"\bsister\b", "brother"));
            replacements.Add((@"\bmother\b", "father"));
            replacements.Add((@"\blady\b", "gentleman"));
            replacements.Add((@"\bqueen\b", "king"));
            replacements.Add((@"\bprincess\b", "prince"));
        }
        else
        {
            return text;
        }

        var result = text;
        foreach (var r in replacements)
        {
            result = System.Text.RegularExpressions.Regex.Replace(result, r.pattern, m => {
                var val = m.Value;
                if (char.IsUpper(val[0]))
                {
                    if (val.Length > 1 && char.IsUpper(val[1]))
                    {
                        return r.replacement.ToUpperInvariant();
                    }
                    return char.ToUpper(r.replacement[0]) + r.replacement.Substring(1);
                }
                return r.replacement;
            }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return result;
    }
}

public record GenerateCompanionRequest(string Prompt, string? SdPrompt, string? VoiceId);
public record GenerateProfileRequest(string Prompt);
public record ImportUrlRequest(string Url);
public record GenerateAssetRequest(string AssetType, string Value, string? SdPromptOverride);
public record SelectAssetRequest(string AssetType, string Value);

public class CompanionConfig
{
    public string Name { get; set; } = string.Empty;
    public string? VoiceId { get; set; }
    public string? Description { get; set; }
    public string? Personality { get; set; }
    public string? Scenario { get; set; }
    public string? FirstMessage { get; set; }
    public string? SystemPrompt { get; set; }
    public string? ConversationId { get; set; }
    public string? AvatarPath { get; set; }
    public string? CurrentOutfit { get; set; }
    public string? CurrentLocation { get; set; }
    public string? CurrentMood { get; set; }
    public string? ClothingState { get; set; }
    public string? BodyType { get; set; }
    public string? BodyShape { get; set; }
    public int RelationshipXp { get; set; }
    public int MessageCount { get; set; }
    public string? VrmModelPath { get; set; }
    public System.Collections.Generic.Dictionary<string, string>? Outfits { get; set; }
}

public static class TopicSummarizer
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ActiveTopics = new();

    public static async Task SummarizeConversation(string conversationId, Database db, BackendManager backends)
    {
        try
        {
            var messages = await db.GetMessages(conversationId);
            if (messages == null || messages.Count < 3) return;

            var recentMessages = messages.OrderBy(m => m.CreatedAt).TakeLast(15).ToList();
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Summarize the current active topic, task, user goal, or core point of this conversation in 1 short sentence (e.g. \"User is asking for help debugging a Kotlin compile error\"). If there is no clear active topic, output NONE.");
            sb.AppendLine("Conversation history:");
            foreach (var m in recentMessages)
            {
                sb.AppendLine($"{m.Role}: {m.Content}");
            }

            var promptMessages = new System.Collections.Generic.List<ChatMessage>
            {
                new ChatMessage("system", "You are a precise conversation analyzer. Summarize the user's current topic or scenario in 1 sentence. Refer to the companion/partner as 'companion', NEVER use the words 'assistant' or 'AI'."),
                new ChatMessage("user", sb.ToString())
            };

            using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(25));
            var fullResponse = new System.Text.StringBuilder();
            
            await foreach (var token in backends.StreamChat("default", promptMessages, cts.Token))
            {
                fullResponse.Append(token);
            }

            var summary = fullResponse.ToString().Trim();
            if (!string.IsNullOrEmpty(summary) && !summary.Equals("NONE", System.StringComparison.OrdinalIgnoreCase))
            {
                summary = System.Text.RegularExpressions.Regex.Replace(summary, @"\b(?:an?\s+)?assistant\b", "companion", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                summary = System.Text.RegularExpressions.Regex.Replace(summary, @"\b(?:an?\s+)?AI\b", "companion", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                ActiveTopics[conversationId] = summary;
                System.Console.WriteLine($"[TopicSummarizer] Updated active topic for {conversationId}: {summary}");
            }
        }
        catch (System.Exception ex)
        {
            System.Console.Error.WriteLine($"[TopicSummarizer] Error summarizing {conversationId}: {ex.Message}");
        }
    }
}

public class GroupSaveRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("character_names")]
    public string? CharacterNamesSnake { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("characterNames")]
    public string? CharacterNamesCamel { get; set; }

    public string CharacterNames => CharacterNamesSnake ?? CharacterNamesCamel ?? string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("scenario")]
    public string? Scenario { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("system_prompt")]
    public string? SystemPromptSnake { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("systemPrompt")]
    public string? SystemPromptCamel { get; set; }

    public string? SystemPrompt => SystemPromptSnake ?? SystemPromptCamel;
}

public class GroupMessageSaveRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("sender")]
    public string Sender { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("character_name")]
    public string? CharacterNameSnake { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("characterName")]
    public string? CharacterNameCamel { get; set; }

    public string? CharacterName => CharacterNameSnake ?? CharacterNameCamel;

    [System.Text.Json.Serialization.JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

[ApiController]
[Route("api/groups")]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly Database _db;

    private int UserId => int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "0");

    public GroupsController(Database db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var list = await _db.GetGroups(UserId);
        return Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] GroupSaveRequest req)
    {
        var id = string.IsNullOrEmpty(req.Id) ? (string.IsNullOrEmpty(req.Uuid) ? Guid.NewGuid().ToString("N") : req.Uuid) : req.Id;
        await _db.SaveGroup(UserId, id, req.Name, req.CharacterNames, req.Scenario, req.SystemPrompt);
        return Ok(new { ok = true, id });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        await _db.DeleteGroup(UserId, id);
        return Ok(new { ok = true });
    }

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> GetMessages(string id)
    {
        var messages = await _db.GetGroupMessages(id);
        return Ok(messages);
    }
}
