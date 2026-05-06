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

            CREATE INDEX IF NOT EXISTS idx_conversations_user ON conversations(user_id);
            CREATE INDEX IF NOT EXISTS idx_messages_conv      ON messages(conversation_id);
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
        return MapUser(r)!;
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
}
