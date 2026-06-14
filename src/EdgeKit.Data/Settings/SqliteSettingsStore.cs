using EdgeKit.Core.Settings;
using Microsoft.Data.Sqlite;

namespace EdgeKit.Data.Settings;

/// <summary>
/// 基于 SQLite 的设置键值存储。表结构：Settings(Key TEXT PRIMARY KEY, Value TEXT)。
/// 数据库路径由构造参数注入，首次访问时自动建库建表。
/// </summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly string _connectionString;

    public SqliteSettingsStore(string databasePath)
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
            "CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, string> LoadAll()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public void Set(string key, string value)
    {
        _pending[key] = value;
    }

    public void Save()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO Settings (Key, Value) VALUES ($key, $value) " +
            "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

        var keyParam = command.CreateParameter();
        keyParam.ParameterName = "$key";
        command.Parameters.Add(keyParam);

        var valueParam = command.CreateParameter();
        valueParam.ParameterName = "$value";
        command.Parameters.Add(valueParam);

        foreach (var (key, value) in _pending)
        {
            keyParam.Value = key;
            valueParam.Value = value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        _pending.Clear();
    }

    // 暂存待提交的键值，Save() 时一次性写入。
    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
}