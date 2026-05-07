using Microsoft.Data.Sqlite;
using AshServer.Models;

namespace AshServer.Data;

/// <summary>
/// All SQLite operations for ash-server.
/// Uses a single connection string; operations are async-friendly via thread-pool dispatch.
/// </summary>
public class Database
{
    private readonly string _connectionString;

    public Database(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ── Schema ─────────────────────────────────────────────────────────────

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS users (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                username     TEXT    UNIQUE NOT NULL,
                password_hash TEXT   NOT NULL,
                email        TEXT,
                is_admin     INTEGER DEFAULT 0,
                created_at   TEXT    DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS conversations (
                id           TEXT    PRIMARY KEY,
                user_id      INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                title        TEXT    DEFAULT 'New Conversation',
                created_at   TEXT    DEFAULT (datetime('now')),
                updated_at   TEXT    DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS messages (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT    NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                role            TEXT    NOT NULL,
                content         TEXT    NOT NULL,
                created_at      TEXT    DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS ai_backends (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT    NOT NULL,
                type       TEXT    NOT NULL DEFAULT 'ollama',
                base_url   TEXT    NOT NULL,
                api_key    TEXT,
                enabled    INTEGER DEFAULT 1,
                created_at TEXT    DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS roles (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT    UNIQUE NOT NULL,
                description TEXT    NOT NULL DEFAULT '',
                color       TEXT    NOT NULL DEFAULT '#6366f1',
                is_system   INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT    DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS role_permissions (
                role_id    INTEGER NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
                permission TEXT    NOT NULL,
                PRIMARY KEY (role_id, permission)
            );

            CREATE TABLE IF NOT EXISTS user_roles (
                user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                role_id INTEGER NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
                PRIMARY KEY (user_id, role_id)
            );

            CREATE INDEX IF NOT EXISTS idx_conversations_user ON conversations(user_id);
            CREATE INDEX IF NOT EXISTS idx_messages_conv      ON messages(conversation_id);
            CREATE INDEX IF NOT EXISTS idx_user_roles_user    ON user_roles(user_id);

            CREATE TABLE IF NOT EXISTS mcp_servers (
                id         TEXT    PRIMARY KEY,
                name       TEXT    NOT NULL,
                type       TEXT    NOT NULL DEFAULT 'stdio',
                command    TEXT    NOT NULL DEFAULT '',
                args       TEXT    NOT NULL DEFAULT '[]',
                env        TEXT    NOT NULL DEFAULT '{}',
                url        TEXT    NOT NULL DEFAULT '',
                enabled    INTEGER NOT NULL DEFAULT 1,
                created_at TEXT    DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS external_identities (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id           INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                provider          TEXT    NOT NULL,
                external_id       TEXT    NOT NULL,
                external_username TEXT,
                linked_at         TEXT    DEFAULT (datetime('now')),
                UNIQUE(provider, external_id)
            );

            CREATE TABLE IF NOT EXISTS channel_configs (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                provider         TEXT    NOT NULL,
                guild_id         TEXT,
                channel_id       TEXT    NOT NULL,
                label            TEXT,
                enabled          INTEGER NOT NULL DEFAULT 1,
                allow_unlinked   INTEGER NOT NULL DEFAULT 0,
                unlinked_role_id INTEGER REFERENCES roles(id) ON DELETE SET NULL,
                agent_enabled    INTEGER NOT NULL DEFAULT 1,
                max_turns        INTEGER NOT NULL DEFAULT 10,
                tool_allowlist   TEXT    NOT NULL DEFAULT '[]',
                created_at       TEXT    DEFAULT (datetime('now')),
                UNIQUE(provider, channel_id)
            );

            CREATE TABLE IF NOT EXISTS chat_audit_log (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                provider        TEXT    NOT NULL,
                channel_id      TEXT    NOT NULL,
                external_id     TEXT    NOT NULL,
                external_username TEXT,
                user_id         INTEGER,
                action          TEXT    NOT NULL,
                detail          TEXT,
                created_at      TEXT    DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS link_codes (
                code       TEXT    PRIMARY KEY,
                user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                provider   TEXT    NOT NULL,
                expires_at TEXT    NOT NULL,
                used       INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_ext_identities_user     ON external_identities(user_id);
            CREATE INDEX IF NOT EXISTS idx_ext_identities_provider ON external_identities(provider, external_id);
            CREATE INDEX IF NOT EXISTS idx_channel_configs_provider ON channel_configs(provider, channel_id);
            CREATE INDEX IF NOT EXISTS idx_audit_log_channel        ON chat_audit_log(provider, channel_id, created_at);
            """;

        cmd.ExecuteNonQuery();

        SeedSystemRoles(conn);
    }

    private static void SeedSystemRoles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();

        // Insert system roles (ignore if already exist)
        cmd.CommandText = """
            INSERT OR IGNORE INTO roles (id, name, description, color, is_system) VALUES
                (1, 'user',      'Default role for all users',     '#6366f1', 1),
                (2, 'moderator', 'Can manage users and content',   '#f59e0b', 1),
                (3, 'admin',     'Full administrative access',     '#ef4444', 1);
            """;
        cmd.ExecuteNonQuery();

        // Seed default permissions per system role
        cmd.CommandText = """
            INSERT OR IGNORE INTO role_permissions (role_id, permission) VALUES
                (1, 'api_access'), (1, 'agent_mode'), (1, 'file_upload'), (1, 'history_export'),
                (2, 'api_access'), (2, 'agent_mode'), (2, 'file_upload'), (2, 'history_export'), (2, 'manage_users'),
                (3, 'api_access'), (3, 'agent_mode'), (3, 'file_upload'), (3, 'history_export'),
                (3, 'system_prompt'), (3, 'manage_users'), (3, 'manage_backends'), (3, 'manage_plugins');
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Users ──────────────────────────────────────────────────────────────

    public Task<User?> GetUserByUsername(string username) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, email, is_admin, created_at FROM users WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapUser(r) : (User?)null;
    });

    public Task<User?> GetUserById(int id) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, email, is_admin, created_at FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapUser(r) : (User?)null;
    });

    public Task<User> CreateUser(string username, string passwordHash, string? email, bool isAdmin = false) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (username, password_hash, email, is_admin)
            VALUES ($u, $p, $e, $a);
            SELECT id, username, password_hash, email, is_admin, created_at FROM users WHERE id = last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$p", passwordHash);
        cmd.Parameters.AddWithValue("$e", (object?)email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", isAdmin ? 1 : 0);
        using var r = cmd.ExecuteReader();
        r.Read();
        var user = MapUser(r)!;
        r.Dispose();
        cmd.Dispose();

        // Auto-assign 'user' system role (id=1) to every new user
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "INSERT OR IGNORE INTO user_roles (user_id, role_id) VALUES ($u, 1)";
        cmd2.Parameters.AddWithValue("$u", user.Id);
        cmd2.ExecuteNonQuery();

        return user;
    });

    public Task UpdateUserPassword(int userId, string newHash) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET password_hash = $h WHERE id = $id";
        cmd.Parameters.AddWithValue("$h", newHash);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    });

    public Task UpdateUserEmail(int userId, string email) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET email = $e WHERE id = $id";
        cmd.Parameters.AddWithValue("$e", email);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    });

    public Task<List<User>> GetAllUsers() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, email, is_admin, created_at FROM users ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<User>();
        while (r.Read()) list.Add(MapUser(r)!);
        return list;
    });

    public Task DeleteUser(int userId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    });

    public Task ToggleAdmin(int userId, bool isAdmin) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET is_admin = $a WHERE id = $id";
        cmd.Parameters.AddWithValue("$a", isAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    });

    public Task<int> CountUsers() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    public Task<int> CountRecentUsers(int days) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE created_at >= datetime('now', $d)";
        cmd.Parameters.AddWithValue("$d", $"-{days} days");
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    // ── Conversations ──────────────────────────────────────────────────────

    public Task<string> CreateConversation(int userId, string title = "New Conversation") => Task.Run(() =>
    {
        var id = Guid.NewGuid().ToString();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO conversations (id, user_id, title) VALUES ($id, $u, $t)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.ExecuteNonQuery();
        return id;
    });

    public Task<List<Conversation>> GetConversations(int userId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, title, created_at, updated_at FROM conversations WHERE user_id = $u ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("$u", userId);
        using var r = cmd.ExecuteReader();
        var list = new List<Conversation>();
        while (r.Read()) list.Add(MapConversation(r));
        return list;
    });

    public Task<Conversation?> GetConversation(string id, int userId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, title, created_at, updated_at FROM conversations WHERE id = $id AND user_id = $u";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", userId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapConversation(r) : (Conversation?)null;
    });

    public Task DeleteConversation(string id, int userId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE id = $id AND user_id = $u";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.ExecuteNonQuery();
    });

    public Task RenameConversation(string id, int userId, string title) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET title = $t, updated_at = datetime('now') WHERE id = $id AND user_id = $u";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.ExecuteNonQuery();
    });

    public Task<int> CountConversations() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM conversations";
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    // ── Messages ───────────────────────────────────────────────────────────

    public Task AddMessage(string conversationId, string role, string content) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (conversation_id, role, content) VALUES ($c, $r, $cnt);
            UPDATE conversations SET updated_at = datetime('now') WHERE id = $c;
            """;
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$cnt", content);
        cmd.ExecuteNonQuery();
    });

    public Task<List<Message>> GetMessages(string conversationId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, conversation_id, role, content, created_at FROM messages WHERE conversation_id = $c ORDER BY id";
        cmd.Parameters.AddWithValue("$c", conversationId);
        using var r = cmd.ExecuteReader();
        var list = new List<Message>();
        while (r.Read()) list.Add(MapMessage(r));
        return list;
    });

    public Task<int> CountMessages() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    public Task<int> CountMessagesToday() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE created_at >= date('now')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    public Task<int> CountActiveUsersInDays(int days) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT c.user_id) FROM messages m
            JOIN conversations c ON m.conversation_id = c.id
            WHERE m.created_at >= datetime('now', $d) AND m.role = 'user'
            """;
        cmd.Parameters.AddWithValue("$d", $"-{days} days");
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    // ── AI Backends ────────────────────────────────────────────────────────

    public Task<List<AiBackend>> GetEnabledBackends() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, base_url, api_key, enabled, created_at FROM ai_backends WHERE enabled = 1 ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<AiBackend>();
        while (r.Read()) list.Add(MapBackend(r));
        return list;
    });

    public Task<List<AiBackend>> GetAllBackends() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, base_url, api_key, enabled, created_at FROM ai_backends ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<AiBackend>();
        while (r.Read()) list.Add(MapBackend(r));
        return list;
    });

    public Task<AiBackend> CreateBackend(string name, string type, string baseUrl, string? apiKey) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ai_backends (name, type, base_url, api_key) VALUES ($n, $t, $u, $k);
            SELECT id, name, type, base_url, api_key, enabled, created_at FROM ai_backends WHERE id = last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$u", baseUrl);
        cmd.Parameters.AddWithValue("$k", (object?)apiKey ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        r.Read();
        return MapBackend(r);
    });

    public Task UpdateBackend(int id, string? name, string? baseUrl, string? apiKey) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sets = new List<string>();
        if (name != null) { sets.Add("name = $n"); cmd.Parameters.AddWithValue("$n", name); }
        if (baseUrl != null) { sets.Add("base_url = $u"); cmd.Parameters.AddWithValue("$u", baseUrl); }
        if (apiKey != null) { sets.Add("api_key = $k"); cmd.Parameters.AddWithValue("$k", apiKey); }
        if (sets.Count == 0) return;
        cmd.CommandText = $"UPDATE ai_backends SET {string.Join(", ", sets)} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    public Task DeleteBackend(int id) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ai_backends WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    public Task ToggleBackend(int id, bool enabled) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ai_backends SET enabled = $e WHERE id = $id";
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    // ── MCP Servers ──────────────────────────────────────────────────────────

    private static AshServer.Models.McpServerConfig MapMcpRow(SqliteDataReader r) => new()
    {
        Id      = r.GetString(0),
        Name    = r.GetString(1),
        Type    = r.GetString(2),
        Command = r.GetString(3),
        Args    = System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [],
        Env     = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(5)) ?? new(),
        Url     = r.GetString(6),
        Enabled = r.GetInt32(7) == 1,
    };

    public Task<List<AshServer.Models.McpServerConfig>> GetMcpServers() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, command, args, env, url, enabled FROM mcp_servers ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        var list = new List<AshServer.Models.McpServerConfig>();
        while (r.Read()) list.Add(MapMcpRow(r));
        return list;
    });

    public Task<AshServer.Models.McpServerConfig?> GetMcpServer(string id) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, command, args, env, url, enabled FROM mcp_servers WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapMcpRow(r) : (AshServer.Models.McpServerConfig?)null;
    });

    public Task CreateMcpServer(AshServer.Models.McpServerConfig s) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mcp_servers (id, name, type, command, args, env, url, enabled)
            VALUES ($id, $name, $type, $cmd, $args, $env, $url, $enabled)
            """;
        cmd.Parameters.AddWithValue("$id",      s.Id);
        cmd.Parameters.AddWithValue("$name",    s.Name);
        cmd.Parameters.AddWithValue("$type",    s.Type);
        cmd.Parameters.AddWithValue("$cmd",     s.Command ?? "");
        cmd.Parameters.AddWithValue("$args",    System.Text.Json.JsonSerializer.Serialize(s.Args));
        cmd.Parameters.AddWithValue("$env",     System.Text.Json.JsonSerializer.Serialize(s.Env));
        cmd.Parameters.AddWithValue("$url",     s.Url ?? "");
        cmd.Parameters.AddWithValue("$enabled", s.Enabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    });

    public Task UpdateMcpServer(AshServer.Models.McpServerConfig s) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mcp_servers
            SET name=$name, type=$type, command=$cmd, args=$args, env=$env, url=$url, enabled=$enabled
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id",      s.Id);
        cmd.Parameters.AddWithValue("$name",    s.Name);
        cmd.Parameters.AddWithValue("$type",    s.Type);
        cmd.Parameters.AddWithValue("$cmd",     s.Command ?? "");
        cmd.Parameters.AddWithValue("$args",    System.Text.Json.JsonSerializer.Serialize(s.Args));
        cmd.Parameters.AddWithValue("$env",     System.Text.Json.JsonSerializer.Serialize(s.Env));
        cmd.Parameters.AddWithValue("$url",     s.Url ?? "");
        cmd.Parameters.AddWithValue("$enabled", s.Enabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    });

    public Task DeleteMcpServer(string id) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM mcp_servers WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    public Task ToggleMcpServer(string id, bool enabled) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE mcp_servers SET enabled = $e WHERE id = $id";
        cmd.Parameters.AddWithValue("$e",  enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    // ── Roles ──────────────────────────────────────────────────────────────

    public Task<List<AshServer.Models.RoleWithPermissions>> GetRoles() => Task.Run(() =>
    {
        using var conn = Open();
        var roles = new List<AshServer.Models.RoleWithPermissions>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, color, is_system, created_at FROM roles ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            roles.Add(new AshServer.Models.RoleWithPermissions(
                r.GetInt32(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetInt32(4) == 1, r.GetString(5), []));
        }
        r.Dispose();

        // Fetch permissions for all roles in one query
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT role_id, permission FROM role_permissions ORDER BY role_id";
        using var r2 = cmd2.ExecuteReader();
        var permMap = new Dictionary<int, List<string>>();
        while (r2.Read())
        {
            var rid = r2.GetInt32(0);
            if (!permMap.TryGetValue(rid, out var list)) { list = []; permMap[rid] = list; }
            list.Add(r2.GetString(1));
        }

        return roles.Select(role => role with { Permissions = permMap.GetValueOrDefault(role.Id, []) }).ToList();
    });

    public Task<AshServer.Models.RoleWithPermissions?> GetRole(int id) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, color, is_system, created_at FROM roles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var role = new AshServer.Models.RoleWithPermissions(
            r.GetInt32(0), r.GetString(1), r.GetString(2),
            r.GetString(3), r.GetInt32(4) == 1, r.GetString(5), []);
        r.Dispose();

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT permission FROM role_permissions WHERE role_id = $id";
        cmd2.Parameters.AddWithValue("$id", id);
        using var r2 = cmd2.ExecuteReader();
        var perms = new List<string>();
        while (r2.Read()) perms.Add(r2.GetString(0));
        return (AshServer.Models.RoleWithPermissions?)(role with { Permissions = perms });
    });

    public Task<AshServer.Models.RoleWithPermissions> CreateRole(string name, string description, string color, List<string> permissions) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO roles (name, description, color, is_system) VALUES ($n, $d, $c, 0);
            SELECT id, name, description, color, is_system, created_at FROM roles WHERE id = last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$d", description);
        cmd.Parameters.AddWithValue("$c", color);
        using var r = cmd.ExecuteReader();
        r.Read();
        var role = new AshServer.Models.RoleWithPermissions(
            r.GetInt32(0), r.GetString(1), r.GetString(2),
            r.GetString(3), r.GetInt32(4) == 1, r.GetString(5), []);
        r.Dispose();
        cmd.Dispose();

        SetRolePermissionsSync(conn, role.Id, permissions);
        return role with { Permissions = permissions };
    });

    public Task UpdateRole(int id, string? name, string? description, string? color, List<string>? permissions) => Task.Run(() =>
    {
        using var conn = Open();
        var sets = new List<string>();
        using var cmd = conn.CreateCommand();
        if (name != null)        { sets.Add("name = $n");        cmd.Parameters.AddWithValue("$n", name); }
        if (description != null) { sets.Add("description = $d"); cmd.Parameters.AddWithValue("$d", description); }
        if (color != null)       { sets.Add("color = $c");       cmd.Parameters.AddWithValue("$c", color); }
        if (sets.Count > 0)
        {
            cmd.CommandText = $"UPDATE roles SET {string.Join(", ", sets)} WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        if (permissions != null)
            SetRolePermissionsSync(conn, id, permissions);
    });

    public Task DeleteRole(int id) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Prevent deleting system roles
        cmd.CommandText = "DELETE FROM roles WHERE id = $id AND is_system = 0";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    private static void SetRolePermissionsSync(SqliteConnection conn, int roleId, List<string> permissions)
    {
        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM role_permissions WHERE role_id = $rid";
        del.Parameters.AddWithValue("$rid", roleId);
        del.ExecuteNonQuery();

        foreach (var perm in permissions.Distinct())
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT OR IGNORE INTO role_permissions (role_id, permission) VALUES ($rid, $p)";
            ins.Parameters.AddWithValue("$rid", roleId);
            ins.Parameters.AddWithValue("$p", perm);
            ins.ExecuteNonQuery();
        }
    }

    // ── User ↔ Role assignments ────────────────────────────────────────────

    public Task<List<string>> GetUserRoleNames(int userId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.name FROM roles r
            JOIN user_roles ur ON ur.role_id = r.id
            WHERE ur.user_id = $u ORDER BY r.id
            """;
        cmd.Parameters.AddWithValue("$u", userId);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    public Task<HashSet<string>> GetUserPermissions(int userId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT rp.permission FROM role_permissions rp
            JOIN user_roles ur ON ur.role_id = rp.role_id
            WHERE ur.user_id = $u
            """;
        cmd.Parameters.AddWithValue("$u", userId);
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    });

    public Task<bool> UserHasPermission(int userId, string permission) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM role_permissions rp
            JOIN user_roles ur ON ur.role_id = rp.role_id
            WHERE ur.user_id = $u AND rp.permission = $p
            """;
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$p", permission);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    });

    public Task AssignRole(int userId, int roleId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO user_roles (user_id, role_id) VALUES ($u, $r)";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$r", roleId);
        cmd.ExecuteNonQuery();
    });

    public Task RemoveRole(int userId, int roleId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Don't allow removing the default 'user' role (id=1) — keeps the system sane
        cmd.CommandText = "DELETE FROM user_roles WHERE user_id = $u AND role_id = $r AND $r != 1";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$r", roleId);
        cmd.ExecuteNonQuery();
    });

    // ── Mappers ────────────────────────────────────────────────────────────

    private static User MapUser(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetInt32(4) == 1, r.GetString(5));

    private static Conversation MapConversation(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4));

    private static Message MapMessage(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));

    private static AiBackend MapBackend(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetInt32(5) == 1, r.GetString(6));

    // ── External Identities ────────────────────────────────────────────────

    private static AshServer.Models.ExternalIdentity MapIdentity(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5));

    public Task<List<AshServer.Models.ExternalIdentity>> GetIdentitiesForUser(int userId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, provider, external_id, external_username, linked_at FROM external_identities WHERE user_id = $uid ORDER BY linked_at";
        cmd.Parameters.AddWithValue("$uid", userId);
        using var r = cmd.ExecuteReader();
        var list = new List<AshServer.Models.ExternalIdentity>();
        while (r.Read()) list.Add(MapIdentity(r));
        return list;
    });

    public Task<AshServer.Models.ExternalIdentity?> GetIdentityByExternal(string provider, string externalId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, provider, external_id, external_username, linked_at FROM external_identities WHERE provider = $p AND external_id = $e";
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$e", externalId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapIdentity(r) : (AshServer.Models.ExternalIdentity?)null;
    });

    public Task<AshServer.Models.ExternalIdentity> AddIdentity(int userId, string provider, string externalId, string? externalUsername) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO external_identities (user_id, provider, external_id, external_username)
            VALUES ($uid, $p, $e, $un)
            ON CONFLICT(provider, external_id) DO UPDATE SET user_id=$uid, external_username=$un
            RETURNING id, user_id, provider, external_id, external_username, linked_at
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$p",   provider);
        cmd.Parameters.AddWithValue("$e",   externalId);
        cmd.Parameters.AddWithValue("$un",  (object?)externalUsername ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        r.Read();
        return MapIdentity(r);
    });

    public Task RemoveIdentity(int id) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM external_identities WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    // ── Channel Configs ────────────────────────────────────────────────────

    private static AshServer.Models.ChannelConfig MapChannelConfig(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetInt32(5) == 1, r.GetInt32(6) == 1,
        r.IsDBNull(7) ? null : r.GetInt32(7),
        r.GetInt32(8) == 1, r.GetInt32(9),
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.GetString(10)) ?? [],
        r.GetString(11));

    public Task<List<AshServer.Models.ChannelConfig>> GetChannelConfigs() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, provider, guild_id, channel_id, label, enabled, allow_unlinked, unlinked_role_id, agent_enabled, max_turns, tool_allowlist, created_at FROM channel_configs ORDER BY provider, channel_id";
        using var r = cmd.ExecuteReader();
        var list = new List<AshServer.Models.ChannelConfig>();
        while (r.Read()) list.Add(MapChannelConfig(r));
        return list;
    });

    public Task<AshServer.Models.ChannelConfig?> GetChannelConfig(string provider, string channelId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, provider, guild_id, channel_id, label, enabled, allow_unlinked, unlinked_role_id, agent_enabled, max_turns, tool_allowlist, created_at FROM channel_configs WHERE provider = $p AND channel_id = $c";
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$c", channelId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapChannelConfig(r) : (AshServer.Models.ChannelConfig?)null;
    });

    public Task<AshServer.Models.ChannelConfig> UpsertChannelConfig(AshServer.Models.ChannelConfig cfg) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO channel_configs (provider, guild_id, channel_id, label, enabled, allow_unlinked, unlinked_role_id, agent_enabled, max_turns, tool_allowlist)
            VALUES ($p, $g, $c, $lbl, $en, $au, $ur, $ae, $mt, $ta)
            ON CONFLICT(provider, channel_id) DO UPDATE SET
                guild_id=$g, label=$lbl, enabled=$en, allow_unlinked=$au,
                unlinked_role_id=$ur, agent_enabled=$ae, max_turns=$mt, tool_allowlist=$ta
            RETURNING id, provider, guild_id, channel_id, label, enabled, allow_unlinked, unlinked_role_id, agent_enabled, max_turns, tool_allowlist, created_at
            """;
        cmd.Parameters.AddWithValue("$p",   cfg.Provider);
        cmd.Parameters.AddWithValue("$g",   (object?)cfg.GuildId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c",   cfg.ChannelId);
        cmd.Parameters.AddWithValue("$lbl", (object?)cfg.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$en",  cfg.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$au",  cfg.AllowUnlinked ? 1 : 0);
        cmd.Parameters.AddWithValue("$ur",  (object?)cfg.UnlinkedRoleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ae",  cfg.AgentEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$mt",  cfg.MaxTurns);
        cmd.Parameters.AddWithValue("$ta",  System.Text.Json.JsonSerializer.Serialize(cfg.ToolAllowlist));
        using var r = cmd.ExecuteReader();
        r.Read();
        return MapChannelConfig(r);
    });

    public Task DeleteChannelConfig(int id) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM channel_configs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    // ── Audit Log ──────────────────────────────────────────────────────────

    public Task AddAuditEntry(string provider, string channelId, string externalId, string? externalUsername, int? userId, string action, string? detail = null) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_audit_log (provider, channel_id, external_id, external_username, user_id, action, detail)
            VALUES ($p, $c, $e, $un, $uid, $act, $det)
            """;
        cmd.Parameters.AddWithValue("$p",   provider);
        cmd.Parameters.AddWithValue("$c",   channelId);
        cmd.Parameters.AddWithValue("$e",   externalId);
        cmd.Parameters.AddWithValue("$un",  (object?)externalUsername ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$act", action);
        cmd.Parameters.AddWithValue("$det", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    });

    public Task<List<AshServer.Models.AuditEntry>> GetAuditLog(string? provider = null, string? channelId = null, int limit = 100) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        var where = new List<string>();
        if (provider  != null) { where.Add("provider = $p");    cmd.Parameters.AddWithValue("$p", provider); }
        if (channelId != null) { where.Add("channel_id = $c");  cmd.Parameters.AddWithValue("$c", channelId); }
        cmd.CommandText = $"SELECT id, provider, channel_id, external_id, external_username, user_id, action, detail, created_at FROM chat_audit_log{(where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")} ORDER BY created_at DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AshServer.Models.AuditEntry>();
        while (r.Read()) list.Add(new AshServer.Models.AuditEntry(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt32(5),
            r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8)));
        return list;
    });

    // ── Link Codes ─────────────────────────────────────────────────────────

    public Task SaveLinkCode(string code, int userId, string provider, DateTime expiresAt) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO link_codes (code, user_id, provider, expires_at) VALUES ($code, $uid, $p, $exp)";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$uid",  userId);
        cmd.Parameters.AddWithValue("$p",    provider);
        cmd.Parameters.AddWithValue("$exp",  expiresAt.ToString("o"));
        cmd.ExecuteNonQuery();
    });

    public Task<(int UserId, string Provider)?> ConsumeLinkCode(string code) => Task.Run<(int, string)?>(() =>
    {
        using var conn = Open();
        using var sel  = conn.CreateCommand();
        sel.CommandText = "SELECT user_id, provider, expires_at, used FROM link_codes WHERE code = $code";
        sel.Parameters.AddWithValue("$code", code);
        using var r = sel.ExecuteReader();
        if (!r.Read()) return null;
        if (r.GetInt32(3) == 1) return null;
        if (DateTime.Parse(r.GetString(2)) < DateTime.UtcNow) return null;
        var uid = r.GetInt32(0);
        var prov = r.GetString(1);
        r.Close();
        using var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE link_codes SET used = 1 WHERE code = $code";
        upd.Parameters.AddWithValue("$code", code);
        upd.ExecuteNonQuery();
        return (uid, prov);
    });
}
