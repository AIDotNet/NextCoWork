using System.Text.Json.Nodes;

namespace Ncw.Agent.Storage;

/// <summary>
/// 账户作用域(2.x <c>db/config-profile.ts</c> 的 <c>currentConfigScope</c> 与 <c>workspaceScopeVisible</c>)。
/// 作用域收窄一律写进 SQL 的 WHERE(<see cref="WorkspaceVisible"/>),不在读回来之后再过滤 ——
/// 漏一处就是「别的账户的数据出现在列表里」,而且 <c>LIMIT</c> 之后再过滤还会丢结果。
/// </summary>
internal static class ConfigScope
{
    public const string Local = "local";

    public const string Key = "config-profile.scope";

    /// <summary>SQL 里作用域参数的名字;每条用了 <see cref="WorkspaceVisible"/> 的语句都要绑定它。</summary>
    public const string Param = "$scope";

    /// <summary>当前作用域:kv 表里的 <c>config-profile.scope</c>,没有或坏了就是 local。每次现读,不缓存。</summary>
    public static string Current(AgentDatabase database)
    {
        if (database.Value("SELECT json FROM kv WHERE key = $k", ("$k", Key)) is not string json)
        {
            return Local;
        }

        try
        {
            return JsonNode.Parse(json) is JsonValue value && value.TryGetValue<string>(out var scope) && scope.Length > 0 ? scope : Local;
        }
        catch (System.Text.Json.JsonException)
        {
            return Local;
        }
    }

    /// <summary>
    /// 「当前作用域看得见这个工作区」的 SQL 条件,<paramref name="workspaceId"/> 是个 SQL 表达式(例如 <c>s.workspace_id</c>)。
    /// ★ 两种「无归属」只有 local 能看:id 为空串、或者工作区行不存在(账户作用域下它只可能是已删除的、或别的库的 id)。
    /// </summary>
    public static string WorkspaceVisible(string workspaceId) =>
        $"(EXISTS (SELECT 1 FROM workspaces vw WHERE vw.id = {workspaceId} AND vw.owner = {Param}) " +
        $"OR ({Param} = '{Local}' AND NOT EXISTS (SELECT 1 FROM workspaces vw WHERE vw.id = {workspaceId})))";
}
