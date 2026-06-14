using System;
using System.Globalization;
using EdgeKit.Core.Clipboard;
using Microsoft.Data.Sqlite;

namespace EdgeKit.Data.Clipboard;

/// <summary>
/// 基于 SQLite 的剪贴板历史与分组仓储。与设置/最近项复用同一数据库文件，首次访问自动建表。
/// 表结构：
/// ClipboardItems(Id PK, Kind, Preview, Content, ImagePath, Files, GroupId, Pinned, SourceAppName, SourceProcessPath, Hash UNIQUE, CreatedUtc)。
/// ClipboardGroups(Id PK, Name, ColorHex, SortOrder)。
/// 图片正文落盘到 clipboard-images 目录，库中只存绝对路径；文件列表用换行拼接存一列。
/// </summary>
public sealed class SqliteClipboardRepository : IClipboardRepository
{
    // 非固定记录的保留上限，写入后裁剪（固定项不计入、不被裁剪）。
    private const int MaxUnpinnedItems = 200;

    private readonly string _connectionString;
    private readonly string _imageDirectory;

    public event EventHandler? Changed;

    public SqliteClipboardRepository(string databasePath, string imageDirectory)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _imageDirectory = imageDirectory;
        Directory.CreateDirectory(_imageDirectory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

        EnsureSchema();
    }

    /// <summary>图片落盘目录，供服务层写入图片文件后回填 ImagePath。</summary>
    public string ImageDirectory => _imageDirectory;

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS ClipboardGroups (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "Name TEXT NOT NULL, " +
            "ColorHex TEXT NOT NULL, " +
            "SortOrder INTEGER NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS ClipboardItems (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "Kind INTEGER NOT NULL, " +
            "Preview TEXT NOT NULL, " +
            "Content TEXT NOT NULL, " +
            "ImagePath TEXT NULL, " +
            "Files TEXT NULL, " +
            "GroupId INTEGER NULL, " +
            "Pinned INTEGER NOT NULL DEFAULT 0, " +
            "SourceAppName TEXT NOT NULL DEFAULT '', " +
            "SourceProcessPath TEXT NOT NULL DEFAULT '', " +
            "Hash TEXT NOT NULL UNIQUE, " +
            "CreatedUtc TEXT NOT NULL);";
        command.ExecuteNonQuery();

        EnsureColumn(connection, "ClipboardItems", "SourceAppName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "ClipboardItems", "SourceProcessPath", "TEXT NOT NULL DEFAULT ''");
    }

    public long Add(ClipboardItem item)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        long resultId;

        // 去重：同 Hash 已存在则更新时间置顶，返回既有 Id。
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT Id FROM ClipboardItems WHERE Hash = $hash;";
            existing.Parameters.AddWithValue("$hash", item.Hash);
            var found = existing.ExecuteScalar();

            if (found is not null && found != DBNull.Value)
            {
                resultId = Convert.ToInt64(found, CultureInfo.InvariantCulture);

                using var bump = connection.CreateCommand();
                bump.Transaction = transaction;
                bump.CommandText =
                    "UPDATE ClipboardItems SET CreatedUtc = $created, " +
                    "SourceAppName = $sourceApp, SourceProcessPath = $sourcePath WHERE Id = $id;";
                bump.Parameters.AddWithValue("$created", item.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
                bump.Parameters.AddWithValue("$sourceApp", item.SourceAppName);
                bump.Parameters.AddWithValue("$sourcePath", item.SourceProcessPath);
                bump.Parameters.AddWithValue("$id", resultId);
                bump.ExecuteNonQuery();
            }
            else
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO ClipboardItems (Kind, Preview, Content, ImagePath, Files, GroupId, Pinned, SourceAppName, SourceProcessPath, Hash, CreatedUtc) " +
                    "VALUES ($kind, $preview, $content, $image, $files, $group, $pinned, $sourceApp, $sourcePath, $hash, $created); " +
                    "SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("$kind", (int)item.Kind);
                insert.Parameters.AddWithValue("$preview", item.Preview);
                insert.Parameters.AddWithValue("$content", item.Content);
                insert.Parameters.AddWithValue("$image", (object?)item.ImagePath ?? DBNull.Value);
                insert.Parameters.AddWithValue("$files", item.Files is { Count: > 0 }
                    ? string.Join('\n', item.Files)
                    : (object)DBNull.Value);
                insert.Parameters.AddWithValue("$group", (object?)item.GroupId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$pinned", item.Pinned ? 1 : 0);
                insert.Parameters.AddWithValue("$sourceApp", item.SourceAppName);
                insert.Parameters.AddWithValue("$sourcePath", item.SourceProcessPath);
                insert.Parameters.AddWithValue("$hash", item.Hash);
                insert.Parameters.AddWithValue("$created", item.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
                resultId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        // 裁剪：仅保留最近 MaxUnpinnedItems 条非固定记录，超出的连带图片删除。
        TrimUnpinned(connection, transaction);

        transaction.Commit();
        Changed?.Invoke(this, EventArgs.Empty);
        return resultId;
    }

    public void Delete(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        DeleteImageFile(connection, id);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ClipboardItems WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(long? groupId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 先删图片文件，再删记录。
        using (var query = connection.CreateCommand())
        {
            query.CommandText = groupId is null
                ? "SELECT ImagePath FROM ClipboardItems WHERE ImagePath IS NOT NULL;"
                : "SELECT ImagePath FROM ClipboardItems WHERE GroupId = $group AND ImagePath IS NOT NULL;";
            if (groupId is not null)
            {
                query.Parameters.AddWithValue("$group", groupId.Value);
            }

            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                TryDeleteFile(reader.GetString(0));
            }
        }

        using var command = connection.CreateCommand();
        if (groupId is null)
        {
            command.CommandText = "DELETE FROM ClipboardItems;";
        }
        else
        {
            command.CommandText = "DELETE FROM ClipboardItems WHERE GroupId = $group;";
            command.Parameters.AddWithValue("$group", groupId.Value);
        }

        command.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetPinned(long id, bool pinned)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ClipboardItems SET Pinned = $pinned WHERE Id = $id;";
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MoveToGroup(long id, long? groupId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ClipboardItems SET GroupId = $group WHERE Id = $id;";
        command.Parameters.AddWithValue("$group", (object?)groupId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ClipboardItem> Get(long? groupId, ClipboardItemKind? kind, string? keyword, int limit)
    {
        var result = new List<ClipboardItem>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        var sql =
            "SELECT Id, Kind, Preview, Content, ImagePath, Files, GroupId, Pinned, SourceAppName, SourceProcessPath, Hash, CreatedUtc " +
            "FROM ClipboardItems WHERE 1 = 1";

        if (groupId is not null)
        {
            sql += " AND GroupId = $group";
            command.Parameters.AddWithValue("$group", groupId.Value);
        }

        if (kind is not null)
        {
            sql += " AND Kind = $kind";
            command.Parameters.AddWithValue("$kind", (int)kind.Value);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql += " AND (Preview LIKE $kw OR Content LIKE $kw OR SourceAppName LIKE $kw OR SourceProcessPath LIKE $kw)";
            command.Parameters.AddWithValue("$kw", "%" + keyword.Trim() + "%");
        }

        sql += " ORDER BY Pinned DESC, CreatedUtc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadItem(reader));
        }

        return result;
    }

    public IReadOnlyList<ClipboardGroup> GetGroups()
    {
        var result = new List<ClipboardGroup>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Name, ColorHex, SortOrder FROM ClipboardGroups ORDER BY SortOrder ASC, Id ASC;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ClipboardGroup(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return result;
    }

    public long AddGroup(string name, string colorHex)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO ClipboardGroups (Name, ColorHex, SortOrder) " +
            "VALUES ($name, $color, COALESCE((SELECT MAX(SortOrder) + 1 FROM ClipboardGroups), 0)); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$color", colorHex);
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void RenameGroup(long id, string name)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ClipboardGroups SET Name = $name WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteGroup(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        // 该组下记录的 GroupId 置空（记录保留），再删分组本身。
        using (var detach = connection.CreateCommand())
        {
            detach.Transaction = transaction;
            detach.CommandText = "UPDATE ClipboardItems SET GroupId = NULL WHERE GroupId = $id;";
            detach.Parameters.AddWithValue("$id", id);
            detach.ExecuteNonQuery();
        }

        using (var del = connection.CreateCommand())
        {
            del.Transaction = transaction;
            del.CommandText = "DELETE FROM ClipboardGroups WHERE Id = $id;";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
        }

        transaction.Commit();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ClipboardItem ReadItem(SqliteDataReader reader)
    {
        var files = reader.IsDBNull(5)
            ? null
            : (IReadOnlyList<string>)reader.GetString(5).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var created = DateTime.TryParse(
            reader.GetString(11),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;

        return new ClipboardItem(
            reader.GetInt64(0),
            (ClipboardItemKind)reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            files,
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetInt32(7) != 0,
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            created);
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        using (var query = connection.CreateCommand())
        {
            query.CommandText = $"PRAGMA table_info({table});";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void TrimUnpinned(SqliteConnection connection, SqliteTransaction transaction)
    {
        // 先删掉将被裁剪记录的图片文件。
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT ImagePath FROM ClipboardItems WHERE Pinned = 0 AND ImagePath IS NOT NULL AND Id NOT IN (" +
                "SELECT Id FROM ClipboardItems WHERE Pinned = 0 ORDER BY CreatedUtc DESC LIMIT $limit);";
            query.Parameters.AddWithValue("$limit", MaxUnpinnedItems);

            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                TryDeleteFile(reader.GetString(0));
            }
        }

        using var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText =
            "DELETE FROM ClipboardItems WHERE Pinned = 0 AND Id NOT IN (" +
            "SELECT Id FROM ClipboardItems WHERE Pinned = 0 ORDER BY CreatedUtc DESC LIMIT $limit);";
        trim.Parameters.AddWithValue("$limit", MaxUnpinnedItems);
        trim.ExecuteNonQuery();
    }

    private static void DeleteImageFile(SqliteConnection connection, long id)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT ImagePath FROM ClipboardItems WHERE Id = $id AND ImagePath IS NOT NULL;";
        query.Parameters.AddWithValue("$id", id);
        if (query.ExecuteScalar() is string path)
        {
            TryDeleteFile(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 图片文件可能被占用，忽略删除失败不影响记录删除。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
