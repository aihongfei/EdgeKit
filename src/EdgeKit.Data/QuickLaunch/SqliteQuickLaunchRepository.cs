using System;
using System.Globalization;
using EdgeKit.Core.QuickLaunch;
using Microsoft.Data.Sqlite;

namespace EdgeKit.Data.QuickLaunch;

/// <summary>
/// 基于 SQLite 的快速启动项仓储。
/// </summary>
public sealed class SqliteQuickLaunchRepository : IQuickLaunchRepository
{
    private readonly string _connectionString;

    public SqliteQuickLaunchRepository(string databasePath)
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

    public event EventHandler? Changed;

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS QuickLaunchItems (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "Kind INTEGER NOT NULL, " +
            "Title TEXT NOT NULL, " +
            "Target TEXT NOT NULL COLLATE NOCASE, " +
            "SortOrder INTEGER NOT NULL, " +
            "CreatedUtc TEXT NOT NULL, " +
            "UNIQUE(Kind, Target));";
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<QuickLaunchItem> GetAll()
    {
        var result = new List<QuickLaunchItem>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Kind, Title, Target, SortOrder, CreatedUtc " +
            "FROM QuickLaunchItems ORDER BY SortOrder ASC, Id ASC;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var created = DateTime.TryParse(
                reader.GetString(5),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTime.UtcNow;

            result.Add(new QuickLaunchItem(
                reader.GetInt64(0),
                (QuickLaunchItemKind)reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                created));
        }

        return result;
    }

    public long AddOrUpdate(QuickLaunchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Target))
        {
            return 0;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        var existingId = FindId(connection, transaction, item.Kind, item.Target);
        if (existingId > 0)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE QuickLaunchItems SET Title = $title WHERE Id = $id;";
            update.Parameters.AddWithValue("$title", item.Title);
            update.Parameters.AddWithValue("$id", existingId);
            update.ExecuteNonQuery();

            transaction.Commit();
            Changed?.Invoke(this, EventArgs.Empty);
            return existingId;
        }

        var sortOrder = item.SortOrder > 0 ? item.SortOrder : GetNextSortOrder(connection, transaction);
        var createdUtc = item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc;

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO QuickLaunchItems (Kind, Title, Target, SortOrder, CreatedUtc) " +
            "VALUES ($kind, $title, $target, $sortOrder, $createdUtc); " +
            "SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$kind", (int)item.Kind);
        insert.Parameters.AddWithValue("$title", item.Title);
        insert.Parameters.AddWithValue("$target", item.Target);
        insert.Parameters.AddWithValue("$sortOrder", sortOrder);
        insert.Parameters.AddWithValue("$createdUtc", createdUtc.ToString("o", CultureInfo.InvariantCulture));

        var id = (long)(insert.ExecuteScalar() ?? 0L);

        transaction.Commit();
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void Delete(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM QuickLaunchItems WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var affected = command.ExecuteNonQuery();
        if (affected > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void DeleteByTarget(QuickLaunchItemKind kind, string target)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM QuickLaunchItems WHERE Kind = $kind AND Target = $target;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$target", target);
        var affected = command.ExecuteNonQuery();
        if (affected > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Contains(QuickLaunchItemKind kind, string target)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        return FindId(connection, transaction: null, kind, target) > 0;
    }

    private static long FindId(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        QuickLaunchItemKind kind,
        string target)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Id FROM QuickLaunchItems WHERE Kind = $kind AND Target = $target LIMIT 1;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$target", target);
        return command.ExecuteScalar() is long id ? id : 0;
    }

    private static int GetNextSortOrder(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM QuickLaunchItems;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
