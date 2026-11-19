using Ncw.Agent.Catalog.Discovery;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Templates;

/// <summary>
/// 「添加供应商」弹窗里的一个内置模版(docs/design/07 §3「添加供应商」)。一个端点一个模版:同一家的国际版、Anthropic 兼容端点各是一条,
/// 用 <see cref="Variant"/> 标出(控制端据此显示「国际版」「Anthropic 兼容」这类本地化标签;名字本身是品牌名,不本地化)。
/// </summary>
/// <param name="Id">模版 id,也是新建供应商时建议的 id(<c>deepseek</c>、<c>deepseek-anthropic</c>、<c>moonshot-intl</c>)。</param>
/// <param name="Service">所属的已知服务(<see cref="KnownServices"/> 的 id);控制端用它找 logo。</param>
/// <param name="Group"><see cref="ProviderTemplateGroups"/>。</param>
/// <param name="Variant">null = 这家的默认端点;否则 <see cref="ProviderTemplateVariants"/>。</param>
/// <param name="KeyUrl">去哪里拿 API Key;不需要 key 的为 null。</param>
/// <param name="NeedsKey">本机服务(Ollama、LM Studio)不要 key。</param>
/// <param name="Featured">「常用」分组里也放一份。</param>
public sealed record ProviderTemplate(
    string Id,
    string Service,
    string Name,
    string Group,
    string? Variant,
    WireProtocol Protocol,
    string BaseUrl,
    string? KeyUrl,
    bool NeedsKey,
    bool Featured);

public static class ProviderTemplateGroups
{
    public const string International = "intl";
    public const string China = "cn";
    public const string Aggregator = "aggregator";
    public const string Local = "local";

    public static readonly IReadOnlyList<string> All = [International, China, Aggregator, Local];
}

public static class ProviderTemplateVariants
{
    /// <summary>国际站(默认端点是国内站的那几家)。</summary>
    public const string International = "intl";

    /// <summary>Anthropic 兼容端点(DeepSeek、Kimi、智谱、Z.ai、MiniMax 给 Claude Code 用的那个)。</summary>
    public const string Anthropic = "anthropic";

    public static readonly IReadOnlyList<string> All = [International, Anthropic];
}

/// <summary>
/// 内置模版表。服务本身(名字、默认协议和地址)来自自动发现共用的 <see cref="KnownServices"/>,这里只补「添加供应商」需要的:分组、拿 key 的地址、其余端点。
/// 每个已知服务都必须在 <see cref="Meta"/> 里有一条(测试保证),这样两边说的是同一张表。
/// </summary>
public static class ProviderTemplates
{
    private sealed record Endpoint(string Variant, WireProtocol Protocol, string BaseUrl, string? KeyUrl = null);

    private sealed record ServiceMeta(string Group, string? KeyUrl, bool Featured = false, params Endpoint[] Extra);

    private const string I = ProviderTemplateGroups.International;
    private const string C = ProviderTemplateGroups.China;
    private const string A = ProviderTemplateGroups.Aggregator;
    private const string L = ProviderTemplateGroups.Local;

    private static readonly Dictionary<string, ServiceMeta> Meta = new(StringComparer.Ordinal)
    {
        ["anthropic"] = new(I, "https://console.anthropic.com/settings/keys", Featured: true),
        ["openai"] = new(I, "https://platform.openai.com/api-keys", Featured: true),
        ["gemini"] = new(I, "https://aistudio.google.com/apikey"),
        ["xai"] = new(I, "https://console.x.ai"),
        ["mistral"] = new(I, "https://console.mistral.ai/api-keys"),
        ["groq"] = new(I, "https://console.groq.com/keys"),
        ["cerebras"] = new(I, "https://cloud.cerebras.ai"),
        ["perplexity"] = new(I, "https://www.perplexity.ai/settings/api"),
        ["deepseek"] = new(C, "https://platform.deepseek.com/api_keys", true,
            new Endpoint(ProviderTemplateVariants.Anthropic, WireProtocol.Anthropic, "https://api.deepseek.com/anthropic")),
        ["moonshot"] = new(C, "https://platform.moonshot.cn/console/api-keys", true,
            new Endpoint(ProviderTemplateVariants.International, WireProtocol.OpenAiChat, "https://api.moonshot.ai/v1", "https://platform.moonshot.ai/console/api-keys"),
            new Endpoint(ProviderTemplateVariants.Anthropic, WireProtocol.Anthropic, "https://api.moonshot.cn/anthropic")),
        ["zhipu"] = new(C, "https://open.bigmodel.cn/usercenter/apikeys", false,
            new Endpoint(ProviderTemplateVariants.Anthropic, WireProtocol.Anthropic, "https://open.bigmodel.cn/api/anthropic")),
        ["zai"] = new(I, "https://z.ai/manage-apikey/apikey-list", false,
            new Endpoint(ProviderTemplateVariants.Anthropic, WireProtocol.Anthropic, "https://api.z.ai/api/anthropic")),
        ["dashscope"] = new(C, "https://bailian.console.aliyun.com", false,
            new Endpoint(ProviderTemplateVariants.International, WireProtocol.OpenAiChat, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "https://modelstudio.console.alibabacloud.com")),
        ["volcengine"] = new(C, "https://console.volcengine.com/ark"),
        ["minimax"] = new(C, "https://platform.minimaxi.com", false,
            new Endpoint(ProviderTemplateVariants.International, WireProtocol.OpenAiChat, "https://api.minimax.io/v1", "https://platform.minimax.io"),
            new Endpoint(ProviderTemplateVariants.Anthropic, WireProtocol.Anthropic, "https://api.minimaxi.com/anthropic")),
        ["stepfun"] = new(C, "https://platform.stepfun.com/interface-key"),
        ["hunyuan"] = new(C, "https://console.cloud.tencent.com/hunyuan/api-key"),
        ["qianfan"] = new(C, "https://console.bce.baidu.com/qianfan"),
        ["lingyi"] = new(C, "https://platform.lingyiwanwu.com/apikeys"),
        ["baichuan"] = new(C, "https://platform.baichuan-ai.com/console/apikey"),
        ["openrouter"] = new(A, "https://openrouter.ai/keys", true),
        ["siliconflow"] = new(A, "https://cloud.siliconflow.cn/account/ak", false,
            new Endpoint(ProviderTemplateVariants.International, WireProtocol.OpenAiChat, "https://api.siliconflow.com/v1", "https://cloud.siliconflow.com/account/ak")),
        ["together"] = new(A, "https://api.together.ai/settings/api-keys"),
        ["fireworks"] = new(A, "https://fireworks.ai/account/api-keys"),
        ["nvidia"] = new(A, "https://build.nvidia.com"),
        ["modelscope"] = new(A, "https://modelscope.cn/my/myaccesstoken"),
        ["ollama"] = new(L, null, true),
        ["lmstudio"] = new(L, null),
    };

    /// <summary>全部模版:按 <see cref="KnownServices.All"/> 的顺序,每家先默认端点、再其余端点。</summary>
    public static readonly IReadOnlyList<ProviderTemplate> All = Build();

    /// <summary>已知服务的 id(测试核对:每一家都有默认模版)。</summary>
    public static IReadOnlyList<string> KnownServiceIds { get; } = [.. KnownServices.All.Select(s => s.Id)];

    private static ProviderTemplate[] Build()
    {
        var list = new List<ProviderTemplate>();
        foreach (var service in KnownServices.All)
        {
            // 两边必须是同一张表:新加的已知服务没补元数据就在类型初始化时失败(测试会先撞上)
            var meta = Meta.GetValueOrDefault(service.Id) ?? throw new InvalidOperationException($"Known service '{service.Id}' has no template metadata.");

            var needsKey = service.LocalPort is null;
            list.Add(new ProviderTemplate(service.Id, service.Id, service.Name, meta.Group, null, service.Protocol, service.BaseUrl, meta.KeyUrl, needsKey, meta.Featured));
            foreach (var extra in meta.Extra)
            {
                list.Add(new ProviderTemplate($"{service.Id}-{extra.Variant}", service.Id, service.Name, meta.Group, extra.Variant, extra.Protocol, extra.BaseUrl,
                    extra.KeyUrl ?? meta.KeyUrl, needsKey, false));
            }
        }

        return [.. list];
    }
}
