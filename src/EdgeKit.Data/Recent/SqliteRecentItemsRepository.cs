using System;
using System.Globalization;
using EdgeKit.Core.Recent;
using Microsoft.Data.Sqlite;

namespace EdgeKit.Data.Recent;

/// <summary>
/// 基于 SQLite 的最近使用项仓储。表结构：
/// RecentItems(Kind INT, Key TEXT, Title TEXT, SubTitle TEXT, Glyph TEXT, LastUsedUtc TEXT, UseCount INT, PRIMARY KEY(Kind,Key))。
/// 与设置存储复用同一数据库文件，首次访问时自动建表。
/// </summary>
public sealed class SqliteRecentItemsRepository : IRecentItemsRepository
{
    // 每种类型最多保留的记录条数，写入后裁剪。
    private const int MaxItemsPerKind = 20;

    private readonly string _connectionString;

    public event EventHandler? Changed;

    public SqliteRecentItemsRepository(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS RecentItems (" +
            "Kind INTEGER NOT NULL, " +
            "Key TEXT NOT NULL, " +
            "Title TEXT NOT NULL, " +
            "SubTitle TEXT NOT NULL, " +
            "Glyph TEXT NOT NULL, " +
            "LastUsedUtc TEXT NOT NULL, " +
            "UseCount INTEGER NOT NULL, " +
            "PRIMARY KEY (Kind, Key));";
        command.ExecuteNonQuery();
    }

    public void Touch(RecentItem item)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        // upsert：已存在则更新标题/副标题/图标/时间并累加次数。
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                "INSERT INTO RecentItems (Kind, Key, Title, SubTitle, Glyph, LastUsedUtc, UseCount) " +
                "VALUES ($kind, $key, $title, $subtitle, $glyph, $lastused, 1) " +
                "ON CONFLICT(Kind, Key) DO UPDATE SET " +
                "Title = excluded.Title, " +
                "SubTitle = excluded.SubTitle, " +
                "Glyph = excluded.Glyph, " +
                "LastUsedUtc = excluded.LastUsedUtc, " +
                "UseCount = RecentItems.UseCount + 1;";

            upsert.Parameters.AddWithValue("$kind", (int)item.Kind);
            upsert.Parameters.AddWithValue("$key", item.Key);
            upsert.Parameters.AddWithValue("$title", item.Title);
            upsert.Parameters.AddWithValue("$subtitle", item.SubTitle);
            upsert.Parameters.AddWithValue("$glyph", item.Glyph);
            upsert.Parameters.AddWithValue("$lastused", item.LastUsedUtc.ToString("o", CultureInfo.InvariantCulture));
            upsert.ExecuteNonQuery();
        }

        // 裁剪：仅保留该类型最近的 MaxItemsPerKind 条。
        using (var trim = connection.CreateCommand())
        {
            trim.Transaction = transaction;
            trim.CommandText =
                "DELETE FROM RecentItems WHERE Kind = $kind AND Key NOT IN (" +
                "SELECT Key FROM RecentItems WHERE Kind = $kind " +
                "ORDER BY LastUsedUtc DESC LIMIT $limit);";
            trim.Parameters.AddWithValue("$kind", (int)item.Kind);
            trim.Parameters.AddWithValue("$limit", MaxItemsPerKind);
            trim.ExecuteNonQuery();
        }

        transaction.Commit();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<RecentItem> GetRecent(RecentItemKind kind, int limit)
    {
        var result = new List<RecentItem>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Key, Title, SubTitle, Glyph, LastUsedUtc, UseCount " +
            "FROM RecentItems WHERE Kind = $kind " +
            "ORDER BY LastUsedUtc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lastUsed = DateTime.TryParse(
                reader.GetString(4),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTime.UtcNow;

            result.Add(new RecentItem(
                kind,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                lastUsed,
                reader.GetInt32(5)));
        }

        return result;
    }

    public void Clear(RecentItemKind? kind)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        if (kind is null)
        {
            command.CommandText = "DELETE FROM RecentItems;";
        }
        else
        {
            command.CommandText = "DELETE FROM RecentItems WHERE Kind = $kind;";
            command.Parameters.AddWithValue("$kind", (int)kind.Value);
        }

        command.ExecuteNonQuery();

        Changed?.Invoke(this, EventArgs.Empty);
    }
}