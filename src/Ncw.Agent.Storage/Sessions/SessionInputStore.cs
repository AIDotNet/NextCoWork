using System.Text.Json;

namespace Ncw.Agent.Storage.Sessions;

/// <summary>
/// 未发出的输入(草稿 + 插入队列)落盘:kv 表里每会话一行 <c>session.input.&lt;会话 id&gt;</c>(2.x 的 <c>sessionInputKey</c>)。
/// ★ 复用 kv 而不是新建表:查询形状是整行读、整行写,字段还在演进。2.x 的规则:
/// 落盘只含非终态条目(pending / promoted);读回来校验失败 / 过期(30 天)整份丢弃,不做部分修复;
/// 会话删除时这一行跟着删(kv 没有外键,<see cref="SessionRepository.Delete"/> 里手删)。
/// 作用域:会话行存在且属于别的账户的,读不到、写抛异常(会话行不存在 = 草稿先于会话,照常放行)。
/// 已消费回执(<c>session_input_receipts</c>)的读写在 <see cref="SessionRepository"/>,和消息同一个事务。
/// </summary>
public sealed class SessionInputStore(AgentDatabase database, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string Key(string sessionId) => $"session.input.{sessionId}";

    /// <summary>读出并校验;不存在、结构对不上、过期、别的作用域,都返回 null。</summary>
    public SessionInputState? Read(string sessionId)
    {
        if (ForeignSession(sessionId))
        {
            return null;
        }

        return database.Value("SELECT json FROM kv WHERE key = $k", ("$k", Key(sessionId))) is string json
               && TryParse(json, Now(), out var state) ? state : null;
    }

    /// <summary>写入(整行替换)。时间戳取现在;只留非终态的队列条目;版本号固定为当前版本。</summary>
    public void Write(string sessionId, SessionInputState state)
    {
        if (ForeignSession(sessionId))
        {
            throw new StorageException($"Session {sessionId} belongs to another account scope.");
        }

        var snapshot = new SessionInputState
        {
            V = SessionInputState.Version,
            Draft = state.Draft ?? "",
            Queued = [.. (state.Queued ?? []).Where(q => q.Status is "pending" or "promoted")],
            SavedAt = Now(),
            Extra = state.Extra,
        };
        database.Run("INSERT INTO kv (key, json) VALUES ($k, $j) ON CONFLICT (key) DO UPDATE SET json = excluded.json",
            ("$k", Key(sessionId)), ("$j", JsonSerializer.Serialize(snapshot, StorageJsonContext.Relaxed.SessionInputState)));
    }

    public bool Remove(string sessionId) =>
        !ForeignSession(sessionId) && database.Run("DELETE FROM kv WHERE key = $k", ("$k", Key(sessionId))) > 0;

    /// <summary>解析并校验一份存档(2.x 的 <c>isValidSessionInput</c>)。</summary>
    internal static bool TryParse(string json, long nowMs, out SessionInputState? state)
    {
        try
        {
            state = JsonSerializer.Deserialize(json, StorageJsonContext.Default.SessionInputState);
        }
        catch (JsonException)
        {
            state = null;
        }

        if (state is null || !state.IsValid(nowMs))
        {
            state = null;
            return false;
        }

        return true;
    }

    private long Now() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private bool ForeignSession(string sessionId) => database.Exists($"""
        SELECT 1 FROM sessions s WHERE s.id = $id AND NOT {ConfigScope.WorkspaceVisible("s.workspace_id")}
        """, ("$id", sessionId), (ConfigScope.Param, ConfigScope.Current(database)));
}
