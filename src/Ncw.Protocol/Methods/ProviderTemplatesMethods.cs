using Ncw.Core.Domain;
using PolyType;

namespace Ncw.Protocol.Methods;

// 「添加供应商」的内置模版(docs/design/07 §3「添加供应商」、09 §8.4)。后加的能力 `providers.templates`:
// 模版表的真源在 Agent(和自动发现共用一张已知服务表),控制端打开弹窗时查一次,不进 Store。
// 名字是品牌名,不本地化;分组、端点标签是稳定 key,控制端翻译。

/// <summary>一个内置模版。一个端点一条:同一家的国际版、Anthropic 兼容端点各是一条,用 <see cref="Variant"/> 标出。</summary>
/// <param name="Id">模版 id,也是新建时建议的供应商 id(<c>deepseek</c>、<c>deepseek-anthropic</c>、<c>moonshot-intl</c>)。</param>
/// <param name="Service">所属服务(<c>deepseek</c>、<c>moonshot</c>……);控制端按它找 logo,同一家的几个端点共用一个。</param>
/// <param name="Name">品牌名(不本地化)。</param>
/// <param name="Group"><see cref="ProviderTemplateGroups"/>;开放的字符串,控制端认不出的放进「其他」。</param>
/// <param name="Variant">null = 这家的默认端点;否则 <see cref="ProviderTemplateVariants"/>(开放的字符串)。</param>
/// <param name="Protocol">所属类型下的协议名(<c>anthropic</c> / <c>openai-chat</c> / <c>openai-responses</c> / <c>openai-images</c> / <c>volcengine-images</c> / <c>openai-videos</c> / <c>volcengine-videos</c> / <c>openai-audio</c>)。</param>
/// <param name="KeyUrl">去哪里拿 API Key;不需要 key 的为 null。</param>
/// <param name="NeedsKey">本机服务(Ollama、LM Studio)为 false。</param>
/// <param name="Featured">「常用」分组里也放一份。</param>
/// <param name="Kind">供应商类型:<c>text</c> / <c>image</c> / <c>video</c> / <c>audio</c>(缺省 <c>text</c>)。</param>
public sealed record ProviderTemplateDto(
    string Id,
    string Service,
    string Name,
    string Group,
    string? Variant,
    string Protocol,
    string BaseUrl,
    string? KeyUrl,
    bool NeedsKey,
    bool Featured,
    string Kind = ProviderKinds.Text);

public sealed record ProviderTemplatesResult(ProviderTemplateDto[] Templates);

/// <summary>模版分组。</summary>
public static class ProviderTemplateGroups
{
    public const string International = "intl";
    public const string China = "cn";
    public const string Aggregator = "aggregator";
    public const string Local = "local";

    public static readonly IReadOnlyList<string> All = [International, China, Aggregator, Local];
}

/// <summary>同一家的其余端点。</summary>
public static class ProviderTemplateVariants
{
    public const string International = "intl";
    public const string Anthropic = "anthropic";

    public static readonly IReadOnlyList<string> All = [International, Anthropic];
}

/// <summary>内置模版的方法表(能力 <see cref="Capabilities.ProviderTemplates"/>,承载 本机 + 远程,member 可读,和 <c>providers.list</c> 一样)。</summary>
public static class ProviderTemplatesMethods
{
    /// <summary>全部内置模版(只读,静态数据,随 Agent 版本变)。</summary>
    public static readonly MethodDef<Unit, ProviderTemplatesResult> List =
        new("providers.templates.list", MethodKind.Query, Role.Member, Transports.Any, PayloadShapes.GetShape<Unit>(), PayloadShapes.GetShape<ProviderTemplatesResult>())
        {
            Capability = Capabilities.ProviderTemplates,
        };

    public static readonly IReadOnlyList<MethodDef> All = [List];
}

public static partial class Capabilities
{
    /// <summary>添加供应商的内置模版:<c>providers.templates.*</c>(后加的能力,设备不报就只有「自定义」)。</summary>
    public const string ProviderTemplates = "providers.templates";
}

[GenerateShapeFor<ProviderTemplateDto>]
[GenerateShapeFor<ProviderTemplatesResult>]
public sealed partial class PayloadShapes;
