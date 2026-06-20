using System.Globalization;
using EdgeKit.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace EdgeKit.Data.Tasks;

public sealed class SqliteTaskBoardRepository : ITaskBoardRepository
{
    private readonly string _connectionString;

    public SqliteTaskBoardRepository(string databasePath)
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
            "CREATE TABLE IF NOT EXISTS TaskCards (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "Title TEXT NOT NULL, " +
            "Description TEXT NOT NULL, " +
            "Status INTEGER NOT NULL, " +
            "Priority INTEGER NOT NULL, " +
            "DueLocal TEXT NULL, " +
            "SortOrder INTEGER NOT NULL, " +
            "CreatedUtc TEXT NOT NULL, " +
            "UpdatedUtc TEXT NOT NULL, " +
            "CompletedUtc TEXT NULL);";
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TaskCard> GetAll()
    {
        var result = new List<TaskCard>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Title, Description, Status, Priority, DueLocal, SortOrder, CreatedUtc, UpdatedUtc, CompletedUtc " +
            "FROM TaskCards ORDER BY Status ASC, SortOrder ASC, Id ASC;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadCard(reader));
        }

        return result;
    }

    public long Add(TaskCard card)
    {
        var now = DateTime.UtcNow;
        var created = card.CreatedUtc == default ? now : card.CreatedUtc;
        var updated = card.UpdatedUtc == default ? now : card.UpdatedUtc;
        var sortOrder = card.SortOrder > 0 ? card.SortOrder : GetNextSortOrder(card.Status);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO TaskCards (Title, Description, Status, Priority, DueLocal, SortOrder, CreatedUtc, UpdatedUtc, CompletedUtc) " +
            "VALUES ($title, $description, $status, $priority, $dueLocal, $sortOrder, $createdUtc, $updatedUtc, $completedUtc); " +
            "SELECT last_insert_rowid();";
        AddCardParameters(command, card with { SortOrder = sortOrder, CreatedUtc = created, UpdatedUtc = updated });

        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void Update(TaskCard card)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE TaskCards SET " +
            "Title = $title, Description = $description, Status = $status, Priority = $priority, DueLocal = $dueLocal, " +
            "SortOrder = $sortOrder, CreatedUtc = $createdUtc, UpdatedUtc = $updatedUtc, CompletedUtc = $completedUtc " +
            "WHERE Id = $id;";
        AddCardParameters(command, card with { UpdatedUtc = DateTime.UtcNow });
        command.Parameters.AddWithValue("$id", card.Id);

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
        command.CommandText = "DELETE FROM TaskCards WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        if (command.ExecuteNonQuery() > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Move(long id, TaskCardStatus status, int sortOrder)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE TaskCards SET Status = $status, SortOrder = $sortOrder, UpdatedUtc = $updatedUtc, " +
            "CompletedUtc = CASE WHEN $status = $done THEN COALESCE(CompletedUtc, $updatedUtc) ELSE NULL END " +
            "WHERE Id = $id;";
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$done", (int)TaskCardStatus.Done);
        command.Parameters.AddWithValue("$id", id);

        if (command.ExecuteNonQuery() > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private int GetNextSortOrder(TaskCardStatus status)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(SortOrder), 0) + 10 FROM TaskCards WHERE Status = $status;";
        command.Parameters.AddWithValue("$status", (int)status);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AddCardParameters(SqliteCommand command, TaskCard card)
    {
        command.Parameters.AddWithValue("$title", card.Title.Trim());
        command.Parameters.AddWithValue("$description", card.Description);
        command.Parameters.AddWithValue("$status", (int)card.Status);
        command.Parameters.AddWithValue("$priority", (int)card.Priority);
        command.Parameters.AddWithValue("$dueLocal", card.DueLocal is null
            ? DBNull.Value
            : card.DueLocal.Value.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sortOrder", card.SortOrder);
        command.Parameters.AddWithValue("$createdUtc", card.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedUtc", card.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$completedUtc", card.CompletedUtc is null
            ? DBNull.Value
            : card.CompletedUtc.Value.ToString("o", CultureInfo.InvariantCulture));
    }

    private static TaskCard ReadCard(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            (TaskCardStatus)reader.GetInt32(3),
            (TaskCardPriority)reader.GetInt32(4),
            ReadNullableDateTime(reader, 5),
            reader.GetInt32(6),
            ReadDateTime(reader.GetString(7)),
            ReadDateTime(reader.GetString(8)),
            ReadNullableDateTime(reader, 9));

    private static DateTime ReadDateTime(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;

    private static DateTime? ReadNullableDateTime(SqliteDataReader reader, int index)
        => reader.IsDBNull(index)
            ? null
            : DateTime.TryParse(reader.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
}