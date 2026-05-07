using System.Text.Json;

namespace AshServer.Models;

public record User(
    int Id,
    string Username,
    string PasswordHash,
    string? Email,
    bool IsAdmin,
    string CreatedAt
);

public record Role(
    int Id,
    string Name,
    string Description,
    string Color,
    bool IsSystem,
    string CreatedAt
);

public record RoleWithPermissions(
    int Id,
    string Name,
    string Description,
    string Color,
    bool IsSystem,
    string CreatedAt,
    List<string> Permissions
);

public record Conversation(
    string Id,
    int UserId,
    string Title,
    string CreatedAt,
    string UpdatedAt
);

public record Message(
    int Id,
    string ConversationId,
    string Role,
    string Content,
    string CreatedAt
);

public record AiBackend(
    int Id,
    string Name,
    string Type,        // "ollama" | "openai"
    string BaseUrl,
    string? ApiKey,
    bool Enabled,
    string CreatedAt
);

// ── Auth DTOs ──────────────────────────────────────────────────────────────

public record RegisterRequest(string Username, string Password, string? Email);
public record LoginRequest(string Username, string Password);
public record LoginResponse(string AccessToken, UserInfo User);
public record UserInfo(
    int Id,
    string Username,
    string? Email,
    bool IsAdmin,
    string CreatedAt,
    List<string> Roles,
    List<string> Permissions
);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record ChangeEmailRequest(string Email);

// ── Role DTOs ──────────────────────────────────────────────────────────────

public record RoleCreateRequest(string Name, string? Description, string? Color, List<string>? Permissions);
public record RoleUpdateRequest(string? Name, string? Description, string? Color, List<string>? Permissions);

// ── Chat DTOs ──────────────────────────────────────────────────────────────

public record ChatMessage(string Role, string Content, List<string>? Images = null);

// ── Admin DTOs ─────────────────────────────────────────────────────────────

public record BackendCreateRequest(string Name, string Type, string BaseUrl, string? ApiKey);
public record BackendUpdateRequest(string? Name, string? BaseUrl, string? ApiKey);
public record AdminCreateUserRequest(string Username, string Email, string Password, bool IsAdmin = false);

// ── MCP ────────────────────────────────────────────────────────────────────

public class McpServerConfig
{
    public string Id      { get; init; } = "";
    public string Name    { get; init; } = "";
    public string Type    { get; init; } = "stdio"; // "stdio" | "http"
    public string Command { get; init; } = "";
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = new();
    public string Url     { get; init; } = "";
    public bool   Enabled { get; init; } = true;
}

public record McpTool(string Name, string Description, JsonElement InputSchema);

public record McpServerInfo(
    string Id,
    string Name,
    string Type,
    bool Connected,
    int ToolCount,
    List<string> Tools,
    string? LastError
);

public record McpServerCreateRequest(
    string? Id,
    string Name,
    string Type,
    string? Command,
    List<string>? Args,
    Dictionary<string,string>? Env,
    string? Url,
    bool Enabled = true
);
