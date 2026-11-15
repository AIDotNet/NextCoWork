using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>
/// 扩展域(Skills / 命令 / Hooks)的修订主题(docs/design/09 §8.4)。这几个域的列表是按项目取的(<c>skills.list</c> 等带 workspaceId),
/// 没法放进一个全量快照;所以主题只广播「各域的修订号」(设备上每次成功改动加一,<b>单调</b>),控制端发现修订号变了就重拉自己镜像里持有的那几份列表。
/// 首帧与 <see cref="ExtensionsEvents.Changed"/> 是同一个类型;控制端只收「不更旧」的。MCP 有自己的全量主题(<c>mcp</c> 主题,见 McpMethods),不在这里。
/// </summary>
public sealed record ExtensionsTopicState(long SkillsRev, long CommandsRev, long HooksRev);

public static class ExtensionsEvents
{
    /// <summary>某个域的列表变了(数据是 <see cref="ExtensionsTopicState"/>,全量)。</summary>
    public const string Changed = "extensions.changed";
}

public static class ExtensionsMethods
{
    /// <summary>修订号主题:viewer 起可订阅(修订号本身不含任何配置内容)。</summary>
    public static readonly TopicDef<ExtensionsTopicState> Topic = new("extensions", PayloadShapes.GetShape<ExtensionsTopicState>());
}


[GenerateShapeFor<ExtensionsTopicState>]
public sealed partial class PayloadShapes;
