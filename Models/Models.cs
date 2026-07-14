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

// ── External Identity / Channel ────────────────────────────────────────────

public record ExternalIdentity(
    int Id,
    int UserId,
    string Provider,
    string ExternalId,
    string? ExternalUsername,
    string LinkedAt
);

public record ChannelConfig(
    int Id,
    string Provider,
    string? GuildId,
    string ChannelId,
    string? Label,
    bool Enabled,
    bool AllowUnlinked,
    int? UnlinkedRoleId,
    bool AgentEnabled,
    int MaxTurns,
    List<string> ToolAllowlist,
    string CreatedAt
);

public record AuditEntry(
    int Id,
    string Provider,
    string ChannelId,
    string ExternalId,
    string? ExternalUsername,
    int? UserId,
    string Action,
    string? Detail,
    string CreatedAt
);

// ── Identity DTOs ──────────────────────────────────────────────────────────

public record AdminLinkRequest(string Provider, string ExternalId, string? ExternalUsername);

public record LinkCodeRequest(string Provider);
public record LinkCodeResponse(string Code, string ExpiresAt, string Instructions);
public record LinkConfirmRequest(string Code, string ExternalId, string? ExternalUsername);

public record ChannelConfigRequest(
    string Provider,
    string ChannelId,
    string? GuildId,
    string? Label,
    bool Enabled = true,
    bool AllowUnlinked = false,
    int? UnlinkedRoleId = null,
    bool AgentEnabled = true,
    int MaxTurns = 10,
    List<string>? ToolAllowlist = null
);

// ── Resolved identity for chat handlers ───────────────────────────────────

public record ResolvedIdentity(
    int? UserId,
    string? Username,
    List<string> Permissions,
    bool IsLinked,
    bool AgentAllowed,
    int MaxTurns,
    string DenyReason   // empty = allowed
)
{
    public bool IsAllowed => string.IsNullOrEmpty(DenyReason);
}

// ── Third-Party Chat Provider Config ────────────────────────────────────────

public record DiscordProviderConfig(
    bool Enabled,
    string BotToken,
    string ApplicationId,
    string CommandPrefix,
    string? StatusText
);

public record SlackProviderConfig(
    bool Enabled,
    string BotToken,
    string AppToken,
    string SigningSecret
);

public record TelegramProviderConfig(
    bool Enabled,
    string BotToken,
    string? WebhookUrl
);

public record ThirdPartyChatConfig(
    string BotLinkSecret,
    DiscordProviderConfig Discord,
    SlackProviderConfig Slack,
    TelegramProviderConfig Telegram
);

// DTOs for saving (tokens may be masked — only update if not a mask placeholder)
public record SaveDiscordProviderRequest(bool Enabled, string? BotToken, string? ApplicationId, string? CommandPrefix, string? StatusText);
public record SaveSlackProviderRequest(bool Enabled, string? BotToken, string? AppToken, string? SigningSecret);
public record SaveTelegramProviderRequest(bool Enabled, string? BotToken, string? WebhookUrl);
public record SaveThirdPartyChatRequest(
    string? BotLinkSecret,
    SaveDiscordProviderRequest? Discord,
    SaveSlackProviderRequest? Slack,
    SaveTelegramProviderRequest? Telegram
);

// ── Unified Database Sync DTOs ─────────────────────────────────────────────

public record SyncMemory(
    int Id,
    int UserId,
    string CompanionName,
    string Content,
    string Category,
    string CreatedAt
);

public record SyncDiary(
    int Id,
    int UserId,
    string CompanionName,
    string DateString,
    string Content,
    string CreatedAt
);

public record SyncGroup(
    string Id,
    int UserId,
    string Name,
    string CharacterNames,
    string CreatedAt
);

public record SyncGroupMessage(
    int Id,
    string GroupId,
    string Sender,
    string? CharacterName,
    string Content,
    string CreatedAt
);

public class CompanionRepository
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? LastSynced { get; set; }
    public string? CreatedAt { get; set; }
}

public class CompanionRegistryManifest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Version { get; set; }
    public List<CompanionRegistryItem> Companions { get; set; } = new();
}

public class CompanionRegistryItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}
