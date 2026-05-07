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

