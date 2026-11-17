using System.IO;
using Microsoft.Data.Sqlite;

namespace DevChat.App.Services;

public sealed record ChatRow(long Id, string Title, string? AgentRole, string? SystemPrompt, string? ProviderChatUrl, DateTime UpdatedAt);
public sealed record MessageRow(long Id, long ChatId, string Role, string Text, DateTime CreatedAt);

/// <summary>
/// История чатов в SQLite (%AppData%\DevChat\DevChat.db).
/// agent_role/system_prompt — зарезервированы под агентский режим (M4):
/// агент = профиль + выделенный чат провайдера.
/// </summary>
public sealed class HistoryService : IDisposable
{
    private readonly SqliteConnection _conn;

    public HistoryService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevChat");
        Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={Path.Combine(dir, "DevChat.db")}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chats (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL DEFAULT 'Новый чат',
                agent_role TEXT,
                system_prompt TEXT,
                provider_chat_url TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id INTEGER NOT NULL REFERENCES chats(id),
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS file_versions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id INTEGER NOT NULL REFERENCES chats(id),
                path TEXT NOT NULL,
                content TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS team_state (
                chat_id INTEGER PRIMARY KEY REFERENCES chats(id),
                plan TEXT NOT NULL,
                stage TEXT NOT NULL,
                changed_files TEXT NOT NULL DEFAULT '[]',
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();
        // миграция: исходная задача команды (для корректного «продолжить»)
        try
        {
            cmd.CommandText = "ALTER TABLE team_state ADD COLUMN task TEXT NOT NULL DEFAULT ''";
            cmd.ExecuteNonQuery();
        }
        catch { /* колонка уже есть */ }
    }

    public long CreateChat(string? agentRole = null, string? systemPrompt = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO chats (agent_role, system_prompt) VALUES ($role, $prompt); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$role", (object?)agentRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prompt", (object?)systemPrompt ?? DBNull.Value);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public List<ChatRow> ListChats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, agent_role, system_prompt, provider_chat_url, updated_at FROM chats ORDER BY updated_at DESC";
        var list = new List<ChatRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ChatRow(r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), DateTime.Parse(r.GetString(5))));
        return list;
    }

    public List<MessageRow> ListMessages(long chatId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, chat_id, role, text, created_at FROM messages WHERE chat_id = $id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", chatId);
        var list = new List<MessageRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MessageRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), DateTime.Parse(r.GetString(4))));
        return list;
    }

    public void AppendMessage(long chatId, string role, string text)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO messages (chat_id, role, text) VALUES ($c, $r, $t); UPDATE chats SET updated_at = datetime('now') WHERE id = $c";
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$t", text);
        cmd.ExecuteNonQuery();
    }

    public void RenameChat(long chatId, string title)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE chats SET title = $t WHERE id = $c";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.ExecuteNonQuery();
    }

    public void SetProviderChatUrl(long chatId, string url)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE chats SET provider_chat_url = $u WHERE id = $c";
        cmd.Parameters.AddWithValue("$u", url);
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сброс привязки к сессии DeepSeek — следующий обмен начнёт новую сессию.</summary>
    public void ClearProviderChatUrl(long chatId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE chats SET provider_chat_url = NULL WHERE id = $c";
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Журнал отката: оригинал файла перед перезаписью агентом.</summary>
    public void SaveFileVersion(long chatId, string path, string content)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO file_versions (chat_id, path, content) VALUES ($c, $p, $t)";
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$t", content);
        cmd.ExecuteNonQuery();
    }

    public sealed record FileVersion(long Id, string Path, string Content);

    public List<FileVersion> ListFileVersions(long chatId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, content FROM file_versions WHERE chat_id = $c ORDER BY id";
        cmd.Parameters.AddWithValue("$c", chatId);
        var list = new List<FileVersion>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new FileVersion(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>Откат write_file чата в обратном порядке. Возвращает число восстановленных файлов.</summary>
    public int RestoreChatFiles(long chatId, string rootFolder)
    {
        var versions = ListFileVersions(chatId);
        versions.Reverse(); // от поздних к ранним
        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in versions)
        {
            if (!restored.Add(v.Path)) continue; // к более ранней версии этого файла уже вернулись
            var full = Path.GetFullPath(Path.Combine(rootFolder, v.Path));
            if (!full.StartsWith(Path.GetFullPath(rootFolder), StringComparison.OrdinalIgnoreCase)) continue;
            File.WriteAllText(full, v.Content);
        }
        return restored.Count;
    }

    // ── состояние командной задачи (переживает сбои: план + этап + сделанные файлы) ──

    public sealed record TeamState(string Task, string Plan, string Stage, IReadOnlyList<string> ChangedFiles);

    public void SaveTeamState(long chatId, string task, string plan, string stage, IEnumerable<string> changedFiles)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO team_state (chat_id, task, plan, stage, changed_files) VALUES ($c, $t, $p, $s, $f)
            ON CONFLICT(chat_id) DO UPDATE SET task=$t, plan=$p, stage=$s, changed_files=$f, updated_at=datetime('now')
            """;
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$t", task);
        cmd.Parameters.AddWithValue("$p", plan);
        cmd.Parameters.AddWithValue("$s", stage);
        cmd.Parameters.AddWithValue("$f", System.Text.Json.JsonSerializer.Serialize(changedFiles.Distinct().ToList()));
        cmd.ExecuteNonQuery();
    }

    public TeamState? GetTeamState(long chatId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT task, plan, stage, changed_files FROM team_state WHERE chat_id = $c";
        cmd.Parameters.AddWithValue("$c", chatId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var files = new List<string>();
        try { files = System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? new(); } catch { /* пусто */ }
        return new TeamState(r.IsDBNull(0) ? "" : r.GetString(0), r.GetString(1), r.GetString(2), files);
    }

    public void ClearTeamState(long chatId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM team_state WHERE chat_id = $c";
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Удалить чат полностью: сообщения, версии файлов, состояние команды.</summary>
    public void DeleteChat(long chatId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM messages WHERE chat_id = $c;
            DELETE FROM file_versions WHERE chat_id = $c;
            DELETE FROM team_state WHERE chat_id = $c;
            DELETE FROM chats WHERE id = $c;
            """;
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
