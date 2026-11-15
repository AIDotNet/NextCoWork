using System.Security.Cryptography;
using System.Text;

namespace Ncw.Agent.Storage;

/// <summary>一条落库的挂起交互(只含元数据,不含工具输入等内容)。<see cref="Status"/> 是 <c>pending</c> / <c>resolved</c> / <c>cancelled</c>。</summary>
public sealed record JournaledInteraction(string Id, string RunId, string Kind, string Status, string? ResolvedBy, long CreatedAt, long UpdatedAt);

/// <summary>
/// 挂起交互的落库(<c>device_interactions</c> 表,2.x 迁移 31 的结构;docs/design/08.03 §4「挂起的交互持久化」)。
/// 这里只记「有过这次交互、它怎么结束的」:挂起的交互活不过进程重启(重启后 Run 本来就以「被中断」收尾),
/// 所以启动时 <see cref="CancelPending"/> 把仍是 <c>pending</c> 的行标成 <c>cancelled</c>,不会有「界面上永远挂着、谁也答不了」的幽灵交互。
/// 闸门自己的裁决(先到先得、版本号)不依赖这里;落库失败只记诊断。
/// <b>不存内容</b>:工具输入、命令行、问题正文都可能含敏感信息,payload 只有种类和版本这类元数据。
/// </summary>
public sealed class InteractionJournalStore(AgentDatabase database, string deviceId)
{
    /// <summary>3.0 写的行的数据代际(2.x 的行是别的值,互不干扰)。</summary>
    public const string Epoch = "3";

    private const string ScopeEpoch = "0";

    public void Open(string id, string runId, string kind, long version, long createdAtMs)
    {
        var payload = Encoding.UTF8.GetBytes($"{{\"kind\":\"{kind}\",\"version\":{version}}}");
        database.Run("""
            INSERT OR REPLACE INTO device_interactions
              (device_id, data_epoch, config_scope, id, scope_epoch, run_id, kind, payload, payload_digest, status, created_at, updated_at)
            VALUES ($d, $e, $s, $id, $se, $run, $kind, $payload, $digest, 'pending', $at, $at)
            """,
            ("$d", deviceId), ("$e", Epoch), ("$s", ConfigScope.Current(database)), ("$id", id), ("$se", ScopeEpoch), ("$run", runId), ("$kind", kind),
            ("$payload", payload), ("$digest", Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()), ("$at", createdAtMs));
    }

    /// <summary>结算一次交互:有人回答 = <c>resolved</c>(带回答者),否则(中止 / Run 结束)= <c>cancelled</c>。只改仍是 pending 的行。</summary>
    public void Settle(string id, bool answered, string? resolvedBy, long atMs) => database.Run("""
        UPDATE device_interactions SET status = $status, resolved_by = $by, updated_at = $at
         WHERE device_id = $d AND data_epoch = $e AND id = $id AND status = 'pending'
        """,
        ("$status", answered ? "resolved" : "cancelled"), ("$by", resolvedBy), ("$at", atMs), ("$d", deviceId), ("$e", Epoch), ("$id", id));

    /// <summary>启动时收尾:仍是 pending 的行都标成 cancelled。返回被收尾的行数。</summary>
    public int CancelPending(long atMs) => database.Run("""
        UPDATE device_interactions SET status = 'cancelled', updated_at = $at
         WHERE device_id = $d AND data_epoch = $e AND status = 'pending'
        """, ("$at", atMs), ("$d", deviceId), ("$e", Epoch));

    public JournaledInteraction? Get(string id) => database.Row("""
        SELECT id, run_id, kind, status, resolved_by, created_at, updated_at FROM device_interactions
         WHERE device_id = $d AND data_epoch = $e AND id = $id
        """,
        r => new JournaledInteraction(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Text(4), r.Int(5), r.Int(6)),
        ("$d", deviceId), ("$e", Epoch), ("$id", id));

    public IReadOnlyList<JournaledInteraction> List() => database.Rows("""
        SELECT id, run_id, kind, status, resolved_by, created_at, updated_at FROM device_interactions
         WHERE device_id = $d AND data_epoch = $e ORDER BY created_at, id
        """,
        r => new JournaledInteraction(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Text(4), r.Int(5), r.Int(6)),
        ("$d", deviceId), ("$e", Epoch));
}
