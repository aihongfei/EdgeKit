using System.Globalization;
using EdgeKit.Core.Agent;
using Microsoft.Data.Sqlite;

namespace EdgeKit.Data.Agent;

public sealed class SqliteAgentRepository : IAgentRepository
{
    private readonly string _connectionString;

    public SqliteAgentRepository(string databasePath)
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

    public IReadOnlyList<AgentConversation> GetConversations(bool includeArchived = false)
    {
        var result = new List<AgentConversation>();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Title, Mode, Model, CreatedUtc, UpdatedUtc, IsArchived, AgentSessionJson " +
            "FROM AgentConversations " +
            (includeArchived ? string.Empty : "WHERE IsArchived = 0 ") +
            "ORDER BY UpdatedUtc DESC, Id DESC;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadConversation(reader));
        }

        return result;
    }

    public AgentConversationDetail? GetConversation(long id)
    {
        using var connection = OpenConnection();

        AgentConversation? conversation;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT Id, Title, Mode, Model, CreatedUtc, UpdatedUtc, IsArchived, AgentSessionJson " +
                "FROM AgentConversations WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            conversation = reader.Read() ? ReadConversation(reader) : null;
        }

        if (conversation is null)
        {
            return null;
        }

        return new AgentConversationDetail(
            conversation,
            ReadMessages(connection, id),
            ReadToolCalls(connection, id));
    }

    public AgentConversation CreateConversation(string title, AgentConversationMode mode, string model)
    {
        var now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO AgentConversations (Title, Mode, Model, CreatedUtc, UpdatedUtc, IsArchived, AgentSessionJson) " +
            "VALUES ($title, $mode, $model, $created, $updated, 0, ''); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$mode", mode.ToString());
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$created", FormatDate(now));
        command.Parameters.AddWithValue("$updated", FormatDate(now));

        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        Changed?.Invoke(this, EventArgs.Empty);
        return new AgentConversation(id, title, mode, model, now, now, false, string.Empty);
    }

    public void UpdateConversation(long id, string title, AgentConversationMode mode, string model)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AgentConversations SET Title = $title, Mode = $mode, Model = $model, UpdatedUtc = $updated WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$mode", mode.ToString());
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$updated", FormatDate(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void TouchConversation(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AgentConversations SET UpdatedUtc = $updated WHERE Id = $id;";
        command.Parameters.AddWithValue("$updated", FormatDate(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ArchiveConversation(long id, bool archived)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AgentConversations SET IsArchived = $archived, UpdatedUtc = $updated WHERE Id = $id;";
        command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        command.Parameters.AddWithValue("$updated", FormatDate(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteConversation(long id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var sql in new[]
        {
            "DELETE FROM AgentToolCalls WHERE ConversationId = $id;",
            "DELETE FROM AgentMessages WHERE ConversationId = $id;",
            "DELETE FROM AgentConversations WHERE Id = $id;"
        })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveSession(long id, string sessionJson)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AgentConversations SET AgentSessionJson = $session, UpdatedUtc = $updated WHERE Id = $id;";
        command.Parameters.AddWithValue("$session", sessionJson);
        command.Parameters.AddWithValue("$updated", FormatDate(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AgentMessage AddMessage(
        long conversationId,
        AgentMessageRole role,
        string content,
        AgentMessageStatus status = AgentMessageStatus.Complete,
        string error = "")
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var sequence = GetNextSequence(connection, transaction, conversationId);
        var now = DateTime.UtcNow;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO AgentMessages (ConversationId, Role, Content, Sequence, Status, ActivityText, Error, CreatedUtc) " +
            "VALUES ($conversation, $role, $content, $sequence, $status, '', $error, $created); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$role", role.ToString());
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$created", FormatDate(now));

        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var touch = connection.CreateCommand();
        touch.Transaction = transaction;
        touch.CommandText = "UPDATE AgentConversations SET UpdatedUtc = $updated WHERE Id = $id;";
        touch.Parameters.AddWithValue("$updated", FormatDate(now));
        touch.Parameters.AddWithValue("$id", conversationId);
        touch.ExecuteNonQuery();

        transaction.Commit();
        Changed?.Invoke(this, EventArgs.Empty);
        return new AgentMessage(id, conversationId, role, content, sequence, status, string.Empty, error, now);
    }

    public void UpdateMessage(long messageId, string content, AgentMessageStatus status, string error = "", string activityText = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AgentMessages SET Content = $content, Status = $status, ActivityText = $activity, Error = $error WHERE Id = $id;";
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$activity", activityText);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", messageId);
        command.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateMessageActivity(long messageId, AgentMessageStatus status, string activityText, string error = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AgentMessages SET Status = $status, ActivityText = $activity, Error = $error WHERE Id = $id;";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$activity", activityText);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", messageId);
        command.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AgentToolCall AddToolCall(
        long conversationId,
        long? messageId,
        string toolId,
        string toolName,
        AgentToolRisk risk,
        string argumentsJson,
        string argumentsSummary,
        AgentToolApprovalStatus approvalStatus,
        AgentToolExecutionStatus executionStatus)
    {
        var now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO AgentToolCalls (ConversationId, MessageId, ToolId, ToolName, Risk, ArgumentsJson, ArgumentsSummary, ResultSummary, ApprovalStatus, ExecutionStatus, CreatedUtc, StartedUtc, CompletedUtc, Error) " +
            "VALUES ($conversation, $message, $toolId, $toolName, $risk, $argumentsJson, $argumentsSummary, '', $approval, $execution, $created, NULL, NULL, ''); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$message", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolId", toolId);
        command.Parameters.AddWithValue("$toolName", toolName);
        command.Parameters.AddWithValue("$risk", risk.ToString());
        command.Parameters.AddWithValue("$argumentsJson", argumentsJson);
        command.Parameters.AddWithValue("$argumentsSummary", argumentsSummary);
        command.Parameters.AddWithValue("$approval", approvalStatus.ToString());
        command.Parameters.AddWithValue("$execution", executionStatus.ToString());
        command.Parameters.AddWithValue("$created", FormatDate(now));

        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        Changed?.Invoke(this, EventArgs.Empty);
        return new AgentToolCall(
            id,
            conversationId,
            messageId,
            toolId,
            toolName,
            risk,
            argumentsJson,
            argumentsSummary,
            string.Empty,
            approvalStatus,
            executionStatus,
            now,
            null,
            null,
            string.Empty);
    }

    public AgentToolCall? GetToolCall(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, ConversationId, MessageId, ToolId, ToolName, Risk, ArgumentsJson, ArgumentsSummary, ResultSummary, ApprovalStatus, ExecutionStatus, CreatedUtc, StartedUtc, CompletedUtc, Error " +
            "FROM AgentToolCalls WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadToolCall(reader) : null;
    }

    public IReadOnlyList<AgentToolCall> GetPendingToolCalls(long conversationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, ConversationId, MessageId, ToolId, ToolName, Risk, ArgumentsJson, ArgumentsSummary, ResultSummary, ApprovalStatus, ExecutionStatus, CreatedUtc, StartedUtc, CompletedUtc, Error " +
            "FROM AgentToolCalls WHERE ConversationId = $conversation AND ApprovalStatus = $approval " +
            "ORDER BY CreatedUtc ASC, Id ASC;";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$approval", AgentToolApprovalStatus.Pending.ToString());

        var result = new List<AgentToolCall>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadToolCall(reader));
        }

        return result;
    }

    public AgentToolCall UpdateToolCall(
        long id,
        AgentToolApprovalStatus approvalStatus,
        AgentToolExecutionStatus executionStatus,
        string resultSummary,
        string error = "")
    {
        var completed = executionStatus is AgentToolExecutionStatus.Succeeded or AgentToolExecutionStatus.Failed or AgentToolExecutionStatus.Skipped
            ? FormatDate(DateTime.UtcNow)
            : null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AgentToolCalls SET ApprovalStatus = $approval, ExecutionStatus = $execution, ResultSummary = $result, Error = $error, " +
            "StartedUtc = COALESCE(StartedUtc, $started), CompletedUtc = $completed WHERE Id = $id;";
        command.Parameters.AddWithValue("$approval", approvalStatus.ToString());
        command.Parameters.AddWithValue("$execution", executionStatus.ToString());
        command.Parameters.AddWithValue("$result", resultSummary);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$started", FormatDate(DateTime.UtcNow));
        command.Parameters.AddWithValue("$completed", (object?)completed ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        Changed?.Invoke(this, EventArgs.Empty);
        return GetToolCall(id)!;
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS AgentConversations (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "Title TEXT NOT NULL, " +
            "Mode TEXT NOT NULL, " +
            "Model TEXT NOT NULL, " +
            "CreatedUtc TEXT NOT NULL, " +
            "UpdatedUtc TEXT NOT NULL, " +
            "IsArchived INTEGER NOT NULL DEFAULT 0, " +
            "AgentSessionJson TEXT NOT NULL DEFAULT '');" +
            "CREATE TABLE IF NOT EXISTS AgentMessages (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "ConversationId INTEGER NOT NULL, " +
            "Role TEXT NOT NULL, " +
            "Content TEXT NOT NULL, " +
            "Sequence INTEGER NOT NULL, " +
            "Status TEXT NOT NULL, " +
            "ActivityText TEXT NOT NULL DEFAULT '', " +
            "Error TEXT NOT NULL DEFAULT '', " +
            "CreatedUtc TEXT NOT NULL);" +
            "CREATE INDEX IF NOT EXISTS IX_AgentMessages_Conversation_Sequence ON AgentMessages(ConversationId, Sequence);" +
            "CREATE TABLE IF NOT EXISTS AgentToolCalls (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "ConversationId INTEGER NOT NULL, " +
            "MessageId INTEGER NULL, " +
            "ToolId TEXT NOT NULL, " +
            "ToolName TEXT NOT NULL, " +
            "Risk TEXT NOT NULL, " +
            "ArgumentsJson TEXT NOT NULL, " +
            "ArgumentsSummary TEXT NOT NULL, " +
            "ResultSummary TEXT NOT NULL DEFAULT '', " +
            "ApprovalStatus TEXT NOT NULL, " +
            "ExecutionStatus TEXT NOT NULL, " +
            "CreatedUtc TEXT NOT NULL, " +
            "StartedUtc TEXT NULL, " +
            "CompletedUtc TEXT NULL, " +
            "Error TEXT NOT NULL DEFAULT '');" +
            "CREATE INDEX IF NOT EXISTS IX_AgentToolCalls_Conversation ON AgentToolCalls(ConversationId, CreatedUtc);";
        command.ExecuteNonQuery();
        EnsureColumn(connection, "AgentMessages", "ActivityText", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column;";
            check.Parameters.AddWithValue("$table", tableName);
            check.Parameters.AddWithValue("$column", columnName);
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static int GetNextSequence(SqliteConnection connection, SqliteTransaction transaction, long conversationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(Sequence), 0) + 1 FROM AgentMessages WHERE ConversationId = $conversation;";
        command.Parameters.AddWithValue("$conversation", conversationId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<AgentMessage> ReadMessages(SqliteConnection connection, long conversationId)
    {
        var result = new List<AgentMessage>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, ConversationId, Role, Content, Sequence, Status, ActivityText, Error, CreatedUtc " +
            "FROM AgentMessages WHERE ConversationId = $conversation ORDER BY Sequence ASC, Id ASC;";
        command.Parameters.AddWithValue("$conversation", conversationId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadMessage(reader));
        }

        return result;
    }

    private static IReadOnlyList<AgentToolCall> ReadToolCalls(SqliteConnection connection, long conversationId)
    {
        var result = new List<AgentToolCall>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, ConversationId, MessageId, ToolId, ToolName, Risk, ArgumentsJson, ArgumentsSummary, ResultSummary, ApprovalStatus, ExecutionStatus, CreatedUtc, StartedUtc, CompletedUtc, Error " +
            "FROM AgentToolCalls WHERE ConversationId = $conversation ORDER BY CreatedUtc ASC, Id ASC;";
        command.Parameters.AddWithValue("$conversation", conversationId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadToolCall(reader));
        }

        return result;
    }

    private static AgentConversation ReadConversation(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            ParseEnum(reader.GetString(2), AgentConversationMode.Chat),
            reader.GetString(3),
            ParseDate(reader.GetString(4)),
            ParseDate(reader.GetString(5)),
            reader.GetInt32(6) != 0,
            reader.GetString(7));

    private static AgentMessage ReadMessage(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            ParseEnum(reader.GetString(2), AgentMessageRole.User),
            reader.GetString(3),
            reader.GetInt32(4),
            ParseEnum(reader.GetString(5), AgentMessageStatus.Complete),
            reader.GetString(6),
            reader.GetString(7),
            ParseDate(reader.GetString(8)));

    private static AgentToolCall ReadToolCall(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseEnum(reader.GetString(5), AgentToolRisk.ReadOnly),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ParseEnum(reader.GetString(9), AgentToolApprovalStatus.NotRequired),
            ParseEnum(reader.GetString(10), AgentToolExecutionStatus.Pending),
            ParseDate(reader.GetString(11)),
            reader.IsDBNull(12) ? null : ParseDate(reader.GetString(12)),
            reader.IsDBNull(13) ? null : ParseDate(reader.GetString(13)),
            reader.GetString(14));

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static DateTime ParseDate(string value)
        => DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;

    private static string FormatDate(DateTime value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
