using System.Text.Json.Nodes;

namespace Ncw.Agent.Parity.Tests.Compare;

/// <summary>比较的四个部分。</summary>
[Flags]
public enum Section
{
    Events = 1,
    Rows = 2,
    Requests = 4,
    Runs = 8,
    Interactions = 16,
    Files = 32,
}

/// <summary>规则引用的文档条目:<see cref="File"/> 是仓库内的相对路径,<see cref="Quote"/> 必须原样出现在那份文档里(有测试检查,所以规则不可能引用一条不存在的登记)。</summary>
public sealed record DocRef(string File, string Quote);

/// <summary>一次比较里规则能看到的上下文:录制本身(规则可以据此把自己限制在具体的场景形状上)和跨部分的标记(事件部分发现的差异,消息行 / 请求 / 结局部分要跟着改写)。</summary>
public sealed class RuleScope(Recording recording)
{
    public Recording Recording { get; } = recording;

    public HashSet<string> Flags { get; } = [];
}

/// <summary>
/// 一条「预期差异」。必须对应 docs/parity.md 或 docs/design 里已登记的偏离(<see cref="Doc"/>),在这里以带说明的规则声明。
/// 规则只改写它点名的那一类条目(<see cref="Apply"/> 拿到两边的整张列表),不允许「忽略整个字段」之类的大范围放宽:
/// 每条规则的匹配条件都落在具体的条目形状上,并在改写之后仍然让两边逐条比较。
/// </summary>
public sealed class Rule(string id, Section sections, DocRef doc, string why, Func<List<JsonNode>, List<JsonNode>, Section, RuleScope, int> apply)
{
    public string Id { get; } = id;

    public Section Sections { get; } = sections;

    public DocRef Doc { get; } = doc;

    public string Why { get; } = why;

    /// <summary>参数:<c>(2.x 一侧, C# 一侧, 当前部分, 上下文)</c>,原地修改;返回改写 / 放行了多少处。</summary>
    public Func<List<JsonNode>, List<JsonNode>, Section, RuleScope, int> Apply { get; } = apply;
}

/// <summary>某个场景里各条规则各应用了几次。</summary>
public sealed class RuleLedger
{
    public Dictionary<string, int> Applied { get; } = [];

    public void Add(Rule rule, int count)
    {
        if (count > 0)
        {
            Applied[rule.Id] = Applied.GetValueOrDefault(rule.Id) + count;
        }
    }
}
