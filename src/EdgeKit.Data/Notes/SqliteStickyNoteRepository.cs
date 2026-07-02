using System.Globalization;
using EdgeKit.Core.Notes;
using Microsoft.Data.Sqlite;

namespace EdgeKit.Data.Notes;

/// <summary>
/// 便签 SQLite 仓储。自动建表、支持增删改查。
/// </summary>
public sealed class SqliteStickyNoteRepository : IStickyNoteRepository
{
    private readonly string _connectionString;

    public SqliteStickyNoteRepository(string databasePath)
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
            "CREATE TABLE IF NOT EXISTS StickyNotes (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "Content TEXT NOT NULL, " +
            "ColorHex TEXT NOT NULL, " +
            "X REAL NOT NULL, " +
            "Y REAL NOT NULL, " +
            "Width REAL NOT NULL, " +
            "Height REAL NOT NULL, " +
            "IsTopmost INTEGER NOT NULL, " +
            "CreatedUtc TEXT NOT NULL, " +
            "UpdatedUtc TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<StickyNote> GetAll()
    {
        var result = new List<StickyNote>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Content, ColorHex, X, Y, Width, Height, IsTopmost, CreatedUtc, UpdatedUtc " +
            "FROM StickyNotes ORDER BY UpdatedUtc DESC;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadNote(reader));
        }

        return result;
    }

    public StickyNote? GetById(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Content, ColorHex, X, Y, Width, Height, IsTopmost, CreatedUtc, UpdatedUtc " +
            "FROM StickyNotes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNote(reader) : null;
    }

    public long Add(StickyNote note)
    {
        var now = DateTime.UtcNow;
        var created = note.CreatedUtc == default ? now : note.CreatedUtc;
        var updated = note.UpdatedUtc == default ? now : note.UpdatedUtc;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO StickyNotes (Content, ColorHex, X, Y, Width, Height, IsTopmost, CreatedUtc, UpdatedUtc) " +
            "VALUES ($content, $colorHex, $x, $y, $width, $height, $isTopmost, $createdUtc, $updatedUtc); " +
            "SELECT last_insert_rowid();";
        AddNoteParameters(command, note with { CreatedUtc = created, UpdatedUtc = updated });

        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void Update(StickyNote note)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE StickyNotes SET " +
            "Content = $content, ColorHex = $colorHex, X = $x, Y = $y, " +
            "Width = $width, Height = $height, IsTopmost = $isTopmost, " +
            "UpdatedUtc = $updatedUtc WHERE Id = $id;";
        AddNoteParameters(command, note with { UpdatedUtc = DateTime.UtcNow });
        command.Parameters.AddWithValue("$id", note.Id);

        if (command.ExecuteNonQuery() > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Delete(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM StickyNotes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        if (command.ExecuteNonQuery() > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void AddNoteParameters(SqliteCommand command, StickyNote note)
    {
        command.Parameters.AddWithValue("$content", note.Content);
        command.Parameters.AddWithValue("$colorHex", note.ColorHex);
        command.Parameters.AddWithValue("$x", note.X);
        command.Parameters.AddWithValue("$y", note.Y);
        command.Parameters.AddWithValue("$width", note.Width);
        command.Parameters.AddWithValue("$height", note.Height);
        command.Parameters.AddWithValue("$isTopmost", note.IsTopmost ? 1 : 0);
        command.Parameters.AddWithValue("$createdUtc", note.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedUtc", note.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
    }

    private static StickyNote ReadNote(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDouble(3),
            reader.GetDouble(4),
            reader.GetDouble(5),
            reader.GetDouble(6),
            reader.GetInt32(7) != 0,
            ReadDateTime(reader.GetString(8)),
            ReadDateTime(reader.GetString(9)));

    private static DateTime ReadDateTime(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;
}