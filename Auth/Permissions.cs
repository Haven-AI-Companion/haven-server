namespace AshServer.Auth;

/// <summary>
/// All permission strings used in the role-based access control system.
/// Admins (is_admin=true) bypass all permission checks.
/// </summary>
public static class Permissions
{
    // ── Chat & AI ──────────────────────────────────────────────────────────
    /// <summary>Can connect to the chat API and send messages.</summary>
    public const string ApiAccess     = "api_access";
    /// <summary>Can enable AI agent mode (tool-calling loop).</summary>
    public const string AgentMode     = "agent_mode";
    /// <summary>Can upload files (images, code, documents).</summary>
    public const string FileUpload    = "file_upload";
    /// <summary>Can export conversation history.</summary>
    public const string HistoryExport = "history_export";
    /// <summary>Can set a custom system prompt.</summary>
    public const string SystemPrompt  = "system_prompt";

    // ── Administration ─────────────────────────────────────────────────────
    /// <summary>Can manage users (create, delete, assign roles).</summary>
    public const string ManageUsers    = "manage_users";
    /// <summary>Can manage AI backends.</summary>
    public const string ManageBackends = "manage_backends";
    /// <summary>Can manage plugins (toggle, reload).</summary>
    public const string ManagePlugins  = "manage_plugins";

    public static readonly string[] All =
    [
        ApiAccess, AgentMode, FileUpload, HistoryExport, SystemPrompt,
        ManageUsers, ManageBackends, ManagePlugins
    ];

    public static readonly Dictionary<string, string> Labels = new()
    {
        [ApiAccess]      = "API / Chat Access",
        [AgentMode]      = "AI Agent Mode",
        [FileUpload]     = "File Upload",
        [HistoryExport]  = "Export Conversations",
        [SystemPrompt]   = "Custom System Prompt",
        [ManageUsers]    = "Manage Users",
        [ManageBackends] = "Manage AI Backends",
        [ManagePlugins]  = "Manage Plugins",
    };

    public static readonly string[] UserDefault =
        [ApiAccess, AgentMode, FileUpload, HistoryExport];

    public static readonly string[] ModeratorDefault =
        [ApiAccess, AgentMode, FileUpload, HistoryExport, ManageUsers];

    public static readonly string[] AdminDefault =
        All;
}
