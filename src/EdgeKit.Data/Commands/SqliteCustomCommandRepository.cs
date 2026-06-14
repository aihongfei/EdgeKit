using System.Globalization;
using EdgeKit.Core.Commands;
using Microsoft.Data.Sqlite;

namespace EdgeKit.Data.Commands;

/// <summary>基于 SQLite 的自定义命令仓储。</summary>
public sealed class SqliteCustomCommandRepository : ICustomCommandRepository
{
    private readonly string _connectionString;

    public SqliteCustomCommandRepository(string databasePath)
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

    public IReadOnlyList<CustomCommand> GetAll()
    {
        var result = new List<CustomCommand>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Title, CommandText, Arguments, WorkingDirectory, Glyph, Keywords, " +
            "RunAsAdministrator, RequiresConfirmation, CreatedUtc, UpdatedUtc " +
            "FROM CustomCommands ORDER BY Title COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CustomCommand(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7) == 1,
                reader.GetInt32(8) == 1,
                ParseDate(reader.GetString(9)),
                ParseDate(reader.GetString(10))));
        }

        return result;
    }

    public long Add(CustomCommand command)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO CustomCommands " +
            "(Title, CommandText, Arguments, WorkingDirectory, Glyph, Keywords, RunAsAdministrator, RequiresConfirmation, CreatedUtc, UpdatedUtc) " +
            "VALUES ($title, $command, $arguments, $workingDirectory, $glyph, $keywords, $admin, $confirm, $created, $updated); " +
            "SELECT last_insert_rowid();";

        insert.Parameters.AddWithValue("$title", command.Title);
        insert.Parameters.AddWithValue("$command", command.CommandText);
        insert.Parameters.AddWithValue("$arguments", command.Arguments);
        insert.Parameters.AddWithValue("$workingDirectory", command.WorkingDirectory);
        insert.Parameters.AddWithValue("$glyph", command.Glyph);
        insert.Parameters.AddWithValue("$keywords", command.Keywords);
        insert.Parameters.AddWithValue("$admin", command.RunAsAdministrator ? 1 : 0);
        insert.Parameters.AddWithValue("$confirm", command.RequiresConfirmation ? 1 : 0);
        insert.Parameters.AddWithValue("$created", command.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$updated", command.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture));

        var id = (long)(insert.ExecuteScalar() ?? 0L);
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void Delete(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CustomCommands WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS CustomCommands (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "Title TEXT NOT NULL, " +
            "CommandText TEXT NOT NULL, " +
            "Arguments TEXT NOT NULL, " +
            "WorkingDirectory TEXT NOT NULL, " +
            "Glyph TEXT NOT NULL, " +
            "Keywords TEXT NOT NULL, " +
            "RunAsAdministrator INTEGER NOT NULL, " +
            "RequiresConfirmation INTEGER NOT NULL, " +
            "CreatedUtc TEXT NOT NULL, " +
            "UpdatedUtc TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    private static DateTime ParseDate(string value)
        => DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;
}
