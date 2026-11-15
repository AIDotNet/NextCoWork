using PolyType;

namespace Ncw.Protocol.Methods;

// 全局会话搜索(docs/design/10 的 `conversations` 模块 → `search.*`)。真源在设备的消息全文索引(FTS5,2.x 同一张 `messages_fts`)。
// 查询按短语处理(用户输入里的 MATCH 运算符不会破坏语法);只搜<b>顶层</b>会话(子代理转录不是用户的对话);作用域收窄在 SQL 里;读取 viewer 起。

/// <param name="Query">短语;空白 = 没有结果。</param>
/// <param name="WorkspaceId">只搜这个项目;null = 全部项目。指定了看不见的项目 = 空结果。</param>
/// <param name="Limit">1–200,缺省 50。</param>
public sealed record SearchConversationsParams(string Query, string? WorkspaceId = null, int Limit = 50);

/// <param name="Snippet">带 FTS5 的 <c>&lt;mark&gt;</c> 高亮标记(控制端渲染时自己转义其余文本)。</param>
public sealed record SearchHitDto(string SessionId, string WorkspaceId, string MessageId, string Title, string Snippet, long CreatedAt);

public sealed record SearchConversationsResult(SearchHitDto[] Hits);

/// <summary>搜索方法表(能力 <see cref="Capabilities.Search"/>,承载 本机 + 远程,viewer 起)。</summary>
public static class SearchMethods
{
    public static readonly MethodDef<SearchConversationsParams, SearchConversationsResult> Conversations =
        MethodTable.Query<SearchConversationsParams, SearchConversationsResult>("search.conversations", Role.Viewer, Capabilities.Search);

    public static readonly IReadOnlyList<MethodDef> All = [Conversations];
}

[GenerateShapeFor<SearchConversationsParams>]
[GenerateShapeFor<SearchHitDto>]
[GenerateShapeFor<SearchConversationsResult>]
public sealed partial class PayloadShapes;
