using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage;

/// <summary>
/// 仓库共用的小工具:命令一律经 <see cref="AgentDatabase.Command"/> 建(自动挂当前事务),参数统一用命名参数。
/// 每个方法自己持有 <see cref="AgentDatabase.Gate"/>(可重入),读取在锁内读完再返回,不把读取器交给调用方。
/// </summary>
internal static class Sql
{
    public static int Run(this AgentDatabase db, string sql, params (string Name, object? Value)[] args)
    {
        lock (db.Gate)
        {
            using var command = Prepare(db, sql, args);
            return command.ExecuteNonQuery();
        }
    }

    public static object? Value(this AgentDatabase db, string sql, params (string Name, object? Value)[] args)
    {
        lock (db.Gate)
        {
            using var command = Prepare(db, sql, args);
            var value = command.ExecuteScalar();
            return value is DBNull ? null : value;
        }
    }

    public static long Count(this AgentDatabase db, string sql, params (string Name, object? Value)[] args) =>
        db.Value(sql, args) is { } value ? Convert.ToInt64(value) : 0;

    public static bool Exists(this AgentDatabase db, string sql, params (string Name, object? Value)[] args) =>
        db.Value(sql, args) is not null;

    public static List<T> Rows<T>(this AgentDatabase db, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        lock (db.Gate)
        {
            using var command = Prepare(db, sql, args);
            using var reader = command.ExecuteReader();
            var list = new List<T>();
            while (reader.Read())
            {
                list.Add(map(reader));
            }

            return list;
        }
    }

    public static T? Row<T>(this AgentDatabase db, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
        where T : class
    {
        lock (db.Gate)
        {
            using var command = Prepare(db, sql, args);
            using var reader = command.ExecuteReader();
            return reader.Read() ? map(reader) : null;
        }
    }

    public static string? Text(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long Int(this SqliteDataReader reader, int ordinal, long fallback = 0) => reader.IsDBNull(ordinal) ? fallback : reader.GetInt64(ordinal);

    private static SqliteCommand Prepare(AgentDatabase db, string sql, (string Name, object? Value)[] args)
    {
        var command = db.Command(sql);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
