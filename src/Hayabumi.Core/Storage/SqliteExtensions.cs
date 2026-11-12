using Microsoft.Data.Sqlite;

namespace Hayabumi.Core.Storage;

internal static class SqliteExtensions
{
    public static int Execute(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static T? Scalar<T>(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(sql, parameters);
        var value = command.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            return default;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static SqliteCommand CreateCommand(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>
    /// 書き込みトランザクションを開始する。SQL の BEGIN を直接使うため、コマンドごとに Transaction を指定しなくてよい。
    /// </summary>
    public static WriteTransaction BeginWrite(this SqliteConnection connection) => new(connection);

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static long ToUnixMs(this DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromUnixMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}

internal sealed class WriteTransaction : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _completed;

    public WriteTransaction(SqliteConnection connection)
    {
        _connection = connection;
        _connection.Execute("BEGIN IMMEDIATE;");
    }

    public void Commit()
    {
        _connection.Execute("COMMIT;");
        _completed = true;
    }

    public void Dispose()
    {
        if (!_completed)
        {
            _completed = true;
            _connection.Execute("ROLLBACK;");
        }
    }
}
