using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Discovery;

/// <summary>
/// 一个已知的模型服务:按主机名认出来,给建议的 id、名字(品牌名,不是界面文字)、默认协议和地址、它常见的环境变量名,
/// 以及各客户端里给它起的名字(opencode / Crush 的供应商 id、Continue 的 <c>provider</c>、Aider <c>api-key</c> 的前缀)。
/// </summary>
internal sealed record KnownService(
    string Id,
    string Name,
    string[] Hosts,
    WireProtocol Protocol,
    string BaseUrl,
    string[] EnvVars,
    string[] Aliases)
{
    /// <summary>本机服务(不要 key):按端口认。</summary>
    public int? LocalPort { get; init; }
}

/// <summary>已知服务表(docs/design/07 §3「供应商自动发现与导入」)。认不出的地址用主机名当名字。</summary>
internal static class KnownServices
{
    public static readonly KnownService[] All =
    [
        new("anthropic", "Anthropic", ["api.anthropic.com"], WireProtocol.Anthropic, "https://api.anthropic.com", ["ANTHROPIC_API_KEY"], ["anthropic", "claude"]),
        new("openai", "OpenAI", ["api.openai.com"], WireProtocol.OpenAiResponses, "https://api.openai.com/v1", ["OPENAI_API_KEY"], ["openai"]),
        new("deepseek", "DeepSeek", ["api.deepseek.com"], WireProtocol.OpenAiChat, "https://api.deepseek.com", ["DEEPSEEK_API_KEY"], ["deepseek"]),
        new("moonshot", "Kimi (Moonshot)", ["api.moonshot.cn", "api.moonshot.ai"], WireProtocol.OpenAiChat, "https://api.moonshot.cn/v1",
            ["MOONSHOT_API_KEY", "KIMI_API_KEY"], ["moonshotai-cn", "moonshotai", "moonshot", "kimi"]),
        new("zhipu", "Zhipu GLM", ["open.bigmodel.cn"], WireProtocol.OpenAiChat, "https://open.bigmodel.cn/api/paas/v4",
            ["ZHIPUAI_API_KEY", "ZHIPU_API_KEY", "GLM_API_KEY", "BIGMODEL_API_KEY"], ["zhipuai", "zhipu", "glm", "bigmodel"]),
        new("zai", "Z.ai", ["api.z.ai"], WireProtocol.OpenAiChat, "https://api.z.ai/api/paas/v4", ["ZAI_API_KEY"], ["zai", "z-ai"]),
        new("dashscope", "Qwen (DashScope)", ["dashscope.aliyuncs.com", "dashscope-intl.aliyuncs.com"], WireProtocol.OpenAiChat, "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ["DASHSCOPE_API_KEY", "QWEN_API_KEY", "ALIBABA_API_KEY"], ["alibaba", "dashscope", "qwen", "alibaba-cn"]),
        new("openrouter", "OpenRouter", ["openrouter.ai"], WireProtocol.OpenAiChat, "https://openrouter.ai/api/v1", ["OPENROUTER_API_KEY"], ["openrouter"]),
        new("siliconflow", "SiliconFlow", ["api.siliconflow.cn", "api.siliconflow.com"], WireProtocol.OpenAiChat, "https://api.siliconflow.cn/v1",
            ["SILICONFLOW_API_KEY", "SILICON_API_KEY"], ["siliconflow", "siliconflow-cn"]),
        new("groq", "Groq", ["api.groq.com"], WireProtocol.OpenAiChat, "https://api.groq.com/openai/v1", ["GROQ_API_KEY"], ["groq"]),
        new("xai", "xAI", ["api.x.ai"], WireProtocol.OpenAiChat, "https://api.x.ai/v1", ["XAI_API_KEY", "GROK_API_KEY"], ["xai", "grok"]),
        new("gemini", "Google Gemini", ["generativelanguage.googleapis.com"], WireProtocol.OpenAiChat, "https://generativelanguage.googleapis.com/v1beta/openai",
            ["GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENERATIVE_AI_API_KEY"], ["google", "gemini"]),
        new("volcengine", "Volcengine Ark", ["ark.cn-beijing.volces.com"], WireProtocol.OpenAiChat, "https://ark.cn-beijing.volces.com/api/v3",
            ["ARK_API_KEY", "VOLCENGINE_API_KEY", "DOUBAO_API_KEY"], ["volcengine", "doubao", "ark"]),
        new("minimax", "MiniMax", ["api.minimaxi.com", "api.minimax.io", "api.minimax.chat"], WireProtocol.OpenAiChat, "https://api.minimaxi.com/v1", ["MINIMAX_API_KEY"], ["minimax"]),
        new("mistral", "Mistral", ["api.mistral.ai"], WireProtocol.OpenAiChat, "https://api.mistral.ai/v1", ["MISTRAL_API_KEY"], ["mistral"]),
        new("together", "Together AI", ["api.together.xyz"], WireProtocol.OpenAiChat, "https://api.together.xyz/v1", ["TOGETHER_API_KEY", "TOGETHERAI_API_KEY"], ["together", "togetherai"]),
        new("fireworks", "Fireworks AI", ["api.fireworks.ai"], WireProtocol.OpenAiChat, "https://api.fireworks.ai/inference/v1", ["FIREWORKS_API_KEY"], ["fireworks", "fireworks-ai"]),
        new("cerebras", "Cerebras", ["api.cerebras.ai"], WireProtocol.OpenAiChat, "https://api.cerebras.ai/v1", ["CEREBRAS_API_KEY"], ["cerebras"]),
        new("perplexity", "Perplexity", ["api.perplexity.ai"], WireProtocol.OpenAiChat, "https://api.perplexity.ai", ["PERPLEXITY_API_KEY", "PPLX_API_KEY"], ["perplexity"]),
        new("nvidia", "NVIDIA NIM", ["integrate.api.nvidia.com"], WireProtocol.OpenAiChat, "https://integrate.api.nvidia.com/v1", ["NVIDIA_API_KEY", "NGC_API_KEY"], ["nvidia", "nim"]),
        new("stepfun", "StepFun", ["api.stepfun.com"], WireProtocol.OpenAiChat, "https://api.stepfun.com/v1", ["STEPFUN_API_KEY", "STEP_API_KEY"], ["stepfun", "step"]),
        new("hunyuan", "Tencent Hunyuan", ["api.hunyuan.cloud.tencent.com"], WireProtocol.OpenAiChat, "https://api.hunyuan.cloud.tencent.com/v1", ["HUNYUAN_API_KEY"], ["hunyuan", "tencent"]),
        new("qianfan", "Baidu Qianfan", ["qianfan.baidubce.com"], WireProtocol.OpenAiChat, "https://qianfan.baidubce.com/v2", ["QIANFAN_API_KEY"], ["qianfan", "baidu", "ernie"]),
        new("modelscope", "ModelScope", ["api-inference.modelscope.cn"], WireProtocol.OpenAiChat, "https://api-inference.modelscope.cn/v1", ["MODELSCOPE_API_KEY"], ["modelscope"]),
        new("lingyi", "01.AI", ["api.lingyiwanwu.com", "api.01.ai"], WireProtocol.OpenAiChat, "https://api.lingyiwanwu.com/v1", ["YI_API_KEY", "LINGYIWANWU_API_KEY"], ["01ai", "lingyi", "yi"]),
        new("baichuan", "Baichuan", ["api.baichuan-ai.com"], WireProtocol.OpenAiChat, "https://api.baichuan-ai.com/v1", ["BAICHUAN_API_KEY"], ["baichuan"]),
        new("ollama", "Ollama", [], WireProtocol.OpenAiChat, "http://localhost:11434/v1", [], ["ollama"]) { LocalPort = 11434 },
        new("lmstudio", "LM Studio", [], WireProtocol.OpenAiChat, "http://localhost:1234/v1", [], ["lmstudio", "lm-studio"]) { LocalPort = 1234 },
    ];

    public static KnownService? ByHost(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (IsLocal(uri))
        {
            return All.FirstOrDefault(s => s.LocalPort == uri.Port);
        }

        return All.FirstOrDefault(s => s.Hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)));
    }

    public static KnownService? ById(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>各客户端里的供应商名 → 已知服务(不分大小写)。</summary>
    public static KnownService? ByAlias(string name) =>
        All.FirstOrDefault(s => s.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || s.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 地址对应的建议 id 和名字:认得出的服务用它的 id(协议和这家默认协议不同时加后缀,例如 DeepSeek 的 Anthropic 兼容端点 → <c>deepseek-anthropic</c>);
    /// 认不出的用主机名(<c>api.example.com</c> → id <c>example-com</c>、名字 <c>api.example.com</c>);认不出的本机地址带上端口。
    /// </summary>
    public static (string Id, string Name) Identify(string baseUrl, WireProtocol protocol, string? fallbackName = null)
    {
        if (ByHost(baseUrl) is { } known)
        {
            return known.Protocol == protocol || (known.Id == "openai" && protocol == WireProtocol.OpenAiChat)
                ? (known.Id, known.Name)
                : ($"{known.Id}-{protocol.NameOf()}", known.Name);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return ("imported", fallbackName ?? baseUrl);
        }

        var host = IsLocal(uri) ? $"localhost-{uri.Port}" : uri.Host;
        var slug = host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        var id = new string([.. slug.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        return (id.Length == 0 ? "imported" : id, string.IsNullOrWhiteSpace(fallbackName) ? uri.Host : fallbackName.Trim());
    }

    public static bool IsLocal(Uri uri) => uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}
