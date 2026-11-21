using Ncw.Agent.Catalog.Discovery;
using Ncw.Agent.Llm.Media;

namespace Ncw.Agent.Catalog.Templates;

/// <summary>
/// 「添加供应商」弹窗里的一个内置模版(docs/design/07 §3「添加供应商」)。一个端点一个模版:同一家的国际版、Anthropic 兼容端点以及
/// 不同类型的媒体端点(图片 / 视频 / 音频)各是一条,用 <see cref="Kind"/> 与 <see cref="Variant"/> 标出。
/// </summary>
/// <param name="Id">模版 id,也是新建供应商时建议的 id(<c>deepseek</c>、<c>deepseek-anthropic</c>、<c>volcengine-image</c>、<c>volcengine-video</c>)。</param>
/// <param name="Service">所属的已知服务(<see cref="KnownServices"/> 的 id);控制端用它找 logo。</param>
/// <param name="Name">品牌名(不本地化)。</param>
/// <param name="Group"><see cref="ProviderTemplateGroups"/>。</param>
/// <param name="Variant">null = 这家在该类型下的默认端点;否则 <see cref="ProviderTemplateVariants"/>。</param>
/// <param name="Protocol">该模版使用的协议(文本 / 图片 / 视频 / 音频之一)。</param>
/// <param name="KeyUrl">去哪里拿 API Key;不需要 key 的为 null。</param>
/// <param name="NeedsKey">本机服务(Ollama、LM Studio)不要 key。</param>
/// <param name="Featured">在当前类型的「常用」分组里也放一份。</param>
/// <param name="Kind">所属供应商类型(默认文本)。</param>
public sealed record ProviderTemplate(
    string Id,
    string Service,
    string Name,
    string Group,
    string? Variant,
    ProviderProtocol Protocol,
    string BaseUrl,
    string? KeyUrl,
    bool NeedsKey,
    bool Featured,
    ProviderKind Kind = ProviderKind.Text);

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
/// 内置模版表。文本服务本身(名字、默认协议和地址)来自自动发现共用的 <see cref="KnownServices"/>,这里补齐分组、拿 key 的地址、其余端点,
/// 以及图片 / 视频 / 音频三类媒体供应商的内置模版(含火山方舟图片与视频协议)。
/// 每个已知服务都必须在 <see cref="Meta"/> 里有一条(测试保证),这样两边说的是同一张表。
/// </summary>
public static class ProviderTemplates
{
    private sealed record Endpoint(string Variant, ProviderProtocol Protocol, string BaseUrl, string? KeyUrl = null);

    private sealed record MediaEndpoint(string Id, ProviderKind Kind, ProviderProtocol Protocol, string BaseUrl, bool Featured = false, string? KeyUrl = null);

    private sealed record ServiceMeta(string Group, string? KeyUrl, bool Featured = false, Endpoint[]? Extra = null, MediaEndpoint[]? Media = null);

    private const string I = ProviderTemplateGroups.International;
    private const string C = ProviderTemplateGroups.China;
    private const string A = ProviderTemplateGroups.Aggregator;
    private const string L = ProviderTemplateGroups.Local;

    private static readonly Dictionary<string, ServiceMeta> Meta = new(StringComparer.Ordinal)
    {
        ["anthropic"] = new(I, "https://console.anthropic.com/settings/keys", Featured: true),
        ["openai"] = new(I, "https://platform.openai.com/api-keys", Featured: true,
            Media:
            [
                new("openai-image", ProviderKind.Image, ProviderProtocol.OpenAiImages, "https://api.openai.com/v1", Featured: true),
                new("openai-video", ProviderKind.Video, ProviderProtocol.OpenAiVideos, "https://api.openai.com/v1", Featured: true),
                new("openai-audio", ProviderKind.Audio, ProviderProtocol.OpenAiAudio, "https://api.openai.com/v1", Featured: true),
            ]),
        ["gemini"] = new(I, "https://aistudio.google.com/apikey",
            Media:
            [
                new("gemini-image", ProviderKind.Image, ProviderProtocol.OpenAiImages, "https://generativelanguage.googleapis.com/v1beta/openai"),
            ]),
        ["xai"] = new(I, "https://console.x.ai",
            Media:
            [
                new("xai-image", ProviderKind.Image, ProviderProtocol.OpenAiImages, "https://api.x.ai/v1"),
                new("xai-video", ProviderKind.Video, ProviderProtocol.OpenAiVideos, "https://api.x.ai/v1"),
            ]),
        ["mistral"] = new(I, "https://console.mistral.ai/api-keys"),
        ["groq"] = new(I, "https://console.groq.com/keys",
            Media:
            [
                new("groq-audio", ProviderKind.Audio, ProviderProtocol.OpenAiAudio, "https://api.groq.com/openai/v1", Featured: true),
            ]),
        ["cerebras"] = new(I, "https://cloud.cerebras.ai"),
        ["perplexity"] = new(I, "https://www.perplexity.ai/settings/api"),
        ["deepseek"] = new(C, "https://platform.deepseek.com/api_keys", Featured: true,
            Extra: [new(ProviderTemplateVariants.Anthropic, ProviderProtocol.Anthropic, "https://api.deepseek.com/anthropic")]),
        ["moonshot"] = new(C, "https://platform.moonshot.cn/console/api-keys", Featured: true,
            Extra:
            [
                new(ProviderTemplateVariants.International, ProviderProtocol.OpenAiChat, "https://api.moonshot.ai/v1", "https://platform.moonshot.ai/console/api-keys"),
                new(ProviderTemplateVariants.Anthropic, ProviderProtocol.Anthropic, "https://api.moonshot.cn/anthropic"),
            ]),
        ["zhipu"] = new(C, "https://open.bigmodel.cn/usercenter/apikeys",
            Extra: [new(ProviderTemplateVariants.Anthropic, ProviderProtocol.Anthropic, "https://open.bigmodel.cn/api/anthropic")],
            Media:
            [
                new("zhipu-image", ProviderKind.Image, ProviderProtocol.OpenAiImages, "https://open.bigmodel.cn/api/paas/v4"),
            ]),
        ["zai"] = new(I, "https://z.ai/manage-apikey/apikey-list",
            Extra: [new(ProviderTemplateVariants.Anthropic, ProviderProtocol.Anthropic, "https://api.z.ai/api/anthropic")]),
        ["dashscope"] = new(C, "https://bailian.console.aliyun.com",
            Extra: [new(ProviderTemplateVariants.International, ProviderProtocol.OpenAiChat, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "https://modelstudio.console.alibabacloud.com")],
            Media:
            [
                new("dashscope-image", ProviderKind.Image, ProviderProtocol.OpenAiImages, "https://dashscope.aliyuncs.com/compatible-mode/v1"),
                new("dashscope-audio", ProviderKind.Audio, ProviderProtocol.OpenAiAudio, "https://dashscope.aliyuncs.com/compatible-mode/v1"),
            ]),
        ["volcengine"] = new(C, "https://console.volcengine.com/ark",
            Media:
            [
                new("volcengine-image", ProviderKind.Image, ProviderProtocol.VolcengineImages, "https://ark.cn-beijing.volces.com/api/v3", Featured: true),
                new("volcengine-video", ProviderKind.Video, ProviderProtocol.VolcengineVideos, "https://ark.cn-beijing.volces.com/api/v3", Featured: true),
            ]),
        ["minimax"] = new(C, "https://platform.minimaxi.com",
            Extra:
            [
                new(ProviderTemplateVariants.International, ProviderProtocol.OpenAiChat, "https://api.minimax.io/v1", "https://platform.minimax.io"),
                new(ProviderTemplateVariants.Anthropic, ProviderProtocol.Anthropic, "https://api.minimaxi.com/anthropic"),
            ]),
        ["stepfun"] = new(C, "https://platform.stepfun.com/interface-key",
            Media:
            [
                new("stepfun-audio", ProviderKind.Audio, ProviderProtocol.OpenAiAudio, "https://api.stepfun.com/v1"),
            ]),
        ["hunyuan"] = new(C, "https://console.cloud.tencent.com/hunyuan/api-key"),
        ["qianfan"] = new(C, "https://console.bce.baidu.com/qianfan"),
        ["lingyi"] = new(C, "https://platform.lingyiwanwu.com/apikeys"),
        ["baichuan"] = new(C, "https://platform.baichuan-ai.com/console/apikey"),
        ["openrouter"] = new(A, "https://openrouter.ai/keys", Featured: true),
        ["siliconflow"] = new(A, "https://cloud.siliconflow.cn/account/ak",
            Extra: [new(ProviderTemplateVariants.International, ProviderProtocol.OpenAiChat, "https://api.siliconflow.com/v1", "https://cloud.siliconflow.com/account/ak")],
            Media:
            [
                new("siliconflow-image", ProviderKind.Image, ProviderProtocol.OpenAiImages, "https://api.siliconflow.cn/v1", Featured: true),
                new("siliconflow-audio", ProviderKind.Audio, ProviderProtocol.OpenAiAudio, "https://api.siliconflow.cn/v1", Featured: true),
            ]),
        ["together"] = new(A, "https://api.together.ai/settings/api-keys"),
        ["fireworks"] = new(A, "https://fireworks.ai/account/api-keys"),
        ["nvidia"] = new(A, "https://build.nvidia.com"),
        ["modelscope"] = new(A, "https://modelscope.cn/my/myaccesstoken"),
        ["ollama"] = new(L, null, Featured: true),
        ["lmstudio"] = new(L, null),
    };

    /// <summary>全部模版:按 <see cref="KnownServices.All"/> 的顺序,每家先文本默认端点、再文本其余端点,然后图片 / 视频 / 音频模版。</summary>
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
            list.Add(new ProviderTemplate(service.Id, service.Id, service.Name, meta.Group, null, service.Protocol.ToProviderProtocol(), service.BaseUrl, meta.KeyUrl, needsKey, meta.Featured, ProviderKind.Text));
            foreach (var extra in meta.Extra ?? [])
            {
                list.Add(new ProviderTemplate($"{service.Id}-{extra.Variant}", service.Id, service.Name, meta.Group, extra.Variant, extra.Protocol, extra.BaseUrl,
                    extra.KeyUrl ?? meta.KeyUrl, needsKey, false, ProviderKind.Text));
            }
        }

        foreach (var service in KnownServices.All)
        {
            var meta = Meta[service.Id];
            var needsKey = service.LocalPort is null;
            foreach (var media in meta.Media ?? [])
            {
                list.Add(new ProviderTemplate(media.Id, service.Id, service.Name, meta.Group, null, media.Protocol, media.BaseUrl,
                    media.KeyUrl ?? meta.KeyUrl, needsKey, media.Featured, media.Kind));
            }
        }

        return [.. list];
    }
}
