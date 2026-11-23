using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Media;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;
using Ncw.Core.Domain;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 从上游 <c>GET …/models</c> 拉回来的一条。<b>这不是配置,是上游报上来的事实</b>:
/// <see cref="DeclaredKind"/> 仅在上游条目显式声明了类型 / 能力 / 输出模态时非空,供按供应商类型过滤时优先采用。
/// </summary>
public sealed record FetchedModel(string Id, string? DisplayName = null, string? DeclaredKind = null);

/// <summary>「从服务商拉取模型列表」的请求构造、响应解析、失败归类(2.x <c>kernel/upstream/model-list.ts</c>)。纯函数,不发请求。</summary>
public static partial class ModelList
{
    /// <summary>Anthropic 分页一页的上限(官方 max)。直接顶满,不做翻页循环:没有任何一家上游有 1000 个模型。</summary>
    public const int AnthropicPageLimit = 1000;

    /// <summary>
    /// 拉列表的路径。两族协议的版本段约定是反的(Anthropic 的 base 不带 <c>/v1</c>,OpenAI 族的带):
    /// Responses 以及所有媒体协议均共用 OpenAI 族的 <c>/models</c>。
    /// 和 <see cref="WireProtocols.PathOf"/> 是同一套约定的两张表,改一张必须改另一张。
    /// </summary>
    public static string PathOf(WireProtocol protocol) => protocol == WireProtocol.Anthropic ? "/v1/models" : "/models";

    public static string PathOf(ProviderProtocol protocol) => protocol == ProviderProtocol.Anthropic ? "/v1/models" : "/models";

    public sealed record ListRequest(string Url, IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// 拼出「拉模型列表」这一次请求。<paramref name="apiKey"/> 为 null = 不带鉴权头(OpenRouter / DeepInfra 这类免鉴权也能拉):
    /// 传 null 而不是空串,因为空串会拼出 <c>Authorization: Bearer </c>,有些网关会当成「鉴权失败」,让用户去查一把他根本没填的 key。
    /// Anthropic 默认只回 20 条,必须显式要一页大的——漏了它的表现不是报错,是列表少了一截。
    /// </summary>
    public static ListRequest Request(WireProtocol protocol, string baseUrl, string? apiKey) =>
        Request(protocol.ToProviderProtocol(), baseUrl, apiKey);

    public static ListRequest Request(ProviderProtocol protocol, string baseUrl, string? apiKey)
    {
        var url = WireProtocols.JoinUrl(baseUrl, PathOf(protocol));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["accept"] = "application/json" };
        if (protocol == ProviderProtocol.Anthropic)
        {
            headers["anthropic-version"] = "2023-06-01";
            if (apiKey is not null)
            {
                headers["x-api-key"] = apiKey;
            }

            return new ListRequest($"{url}?limit={AnthropicPageLimit}", headers);
        }

        if (apiKey is not null)
        {
            headers["authorization"] = $"Bearer {apiKey}";
        }

        return new ListRequest(url, headers);
    }

    /// <summary>
    /// 「拉模型列表」的完整鉴权形状,和对话请求共用同一套来源(传输装饰器):签名式凭据(今天只有 Ollama Cloud,accessToken 槽里是 SSH 私钥)
    /// <b>绝不带 bearer</b>——2.x 实测事故:拿它拼 <c>Bearer -----BEGIN OPENSSH PRIVATE KEY-----…</c>,多行头抛异常,私钥整段进了错误弹窗。
    /// </summary>
    public static ListRequest RequestFor(ProviderConfig provider, WireProtocol protocol, ProviderCredential? credential, ITransportDecorator transport, RequestContext context)
    {
        var decoration = credential is not null
            ? transport.Decorate(provider, protocol, credential, context)
            : null;
        var signer = credential is OAuthCredential ? decoration?.Signer : null;
        var key = credential is null or SignatureCredential || signer is not null ? null : CredentialFormat.BearerOf(credential);
        var request = Request(protocol, provider.BaseUrl, key);
        var hasHeaders = decoration?.Headers is { Count: > 0 };
        var hasDrops = decoration?.DropHeaders is { Count: > 0 };
        if (signer is null && !hasHeaders && !hasDrops)
        {
            return request;
        }

        var url = request.Url;
        SignedRequest? signed = null;
        if (signer is not null)
        {
            var target = new UriBuilder(request.Url);
            signed = signer.Sign("GET", new Uri(request.Url).AbsolutePath);
            var query = System.Web.HttpUtility.ParseQueryString(target.Query);
            foreach (var (name, value) in signed.Query ?? new Dictionary<string, string>())
            {
                query[name] = value;
            }

            target.Query = query.ToString();
            url = target.Uri.ToString();
        }

        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in decoration?.Headers ?? new Dictionary<string, string>())
        {
            headers[name] = value;
        }

        foreach (var (name, value) in signed?.Headers ?? new Dictionary<string, string>())
        {
            headers[name] = value;
        }

        foreach (var drop in decoration?.DropHeaders ?? [])
        {
            headers.Remove(drop);
        }

        return new ListRequest(url, headers);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]+-----[\s\S]*?-----END [A-Z ]+-----")]
    private static partial Regex PrivateKeyBlock();

    /// <summary>把文本里的密钥材料抹掉(兜底:任何未来的「头里带了不该带的东西」都先在这里被拦成 <c>[redacted key]</c>)。</summary>
    public static string RedactSecrets(string text) => PrivateKeyBlock().Replace(text, "[redacted key]");

    /// <summary>
    /// 解模型列表。容错的对象是「外壳」(<c>{data:[…]}</c> / <c>{models:[…]}</c> / 裸数组),不是「条目」:条目里的 <c>id</c> 是这个协议唯一真正稳定的东西。
    /// 认不出 id 的条目直接丢掉,不给它编一个(编出来的会被当成 upstreamModel 存进别名表,然后在某次真实对话里以 404 出现)。
    /// 保持上游顺序:Anthropic 是新的在前,那个顺序有信息量。同时提取条目中可选的类型 / 能力 / 输出模态供过滤使用。
    /// </summary>
    public static IReadOnlyList<FetchedModel> Parse(WireProtocol protocol, JsonElement payload) =>
        Parse(protocol.ToProviderProtocol(), payload);

    public static IReadOnlyList<FetchedModel> Parse(ProviderProtocol protocol, JsonElement payload)
    {
        var result = new List<FetchedModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Envelope(payload))
        {
            if (IdOf(row) is not { } id || !seen.Add(id))
            {
                continue;
            }

            // OpenAI 族不读 display_name:那是 Anthropic 的字段。
            var name = protocol == ProviderProtocol.Anthropic ? DisplayNameOf(row) : null;
            var declaredKind = DeclaredKindOf(row, id);
            result.Add(new FetchedModel(id, name, declaredKind));
        }

        return result;
    }

    private static IEnumerable<JsonElement> Envelope(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return payload.EnumerateArray();
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var name in new[] { "data", "models" })
        {
            if (payload.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                return array.EnumerateArray();
            }
        }

        return [];
    }

    private static string? IdOf(JsonElement row)
    {
        if (row.ValueKind == JsonValueKind.String)
        {
            return Trimmed(row.GetString());
        }

        return row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? Trimmed(id.GetString())
            : null;
    }

    private static string? DisplayNameOf(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String
            ? Trimmed(n.GetString())
            : null;

    /// <summary>
    /// 从上游 <c>/models</c> 条目中提取显式声明的模型类型(若有)。忽略 OpenAI 标准字段 <c>"object": "model"</c>。
    /// 支持:
    /// 1. <c>output_modalities</c> / <c>modalities.{input,output}</c> / <c>architecture.{input_modalities,output_modalities,modality}</c>;
    /// 2. <c>modality</c> / <c>type</c> / <c>model_type</c> / <c>task_type</c> / <c>domain</c> / <c>endpoint_type</c> / <c>category</c>;
    /// 3. <c>capabilities</c>(数组如 NextCoWork 的 <c>["ImageGeneration", "VisionInput"]</c>,或对象如 <c>{"image_output":true}</c>)。
    /// 注意:<c>capabilities</c> 里的 <c>AudioOutput</c> 只是辅助能力标签(如 Seedance 2.0/2.5 生成有声视频时报 <c>["VisionInput","AudioInput","AudioOutput"]</c>,
    /// 全模态对话模型也会报 <c>AudioOutput</c>),不能单凭它把有声视频模型或对话模型覆盖成纯音频模型。
    /// </summary>
    private static string? DeclaredKindOf(JsonElement row, string modelId)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 1. 显式输入/输出模态数组
        if (TryModalitiesObject(row, "modalities", modelId) is { } fromMod)
        {
            return fromMod;
        }

        if (TryModalitiesObject(row, "architecture", modelId) is { } fromArch)
        {
            return fromArch;
        }

        var topInput = StringArray(row, "input_modalities");
        var topOutput = StringArray(row, "output_modalities");
        if (ModelKindClassifier.FromModalities(topInput, topOutput, modelId) is { } fromTopModalities)
        {
            return fromTopModalities;
        }

        // 2. 标量主类型/任务字段(比辅助 capabilities 标签更权威)
        foreach (var field in new[] { "modality", "model_type", "task_type", "domain", "endpoint_type", "category", "type" })
        {
            if (row.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String
                && ModelKindClassifier.NormalizeDeclared(prop.GetString()) is { } declared)
            {
                return declared;
            }
        }

        // 3. 能力列表/对象(如 NextCoWork /v1/models 的 capabilities)
        if (row.TryGetProperty("capabilities", out var caps) && DeclaredFromCapabilities(caps, modelId) is { } fromCaps)
        {
            return fromCaps;
        }

        return null;
    }

    private static string? TryModalitiesObject(JsonElement row, string property, string modelId)
    {
        if (!row.TryGetProperty(property, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = StringArray(obj, "input") ?? StringArray(obj, "input_modalities");
        var output = StringArray(obj, "output") ?? StringArray(obj, "output_modalities");
        if (ModelKindClassifier.FromModalities(input, output, modelId) is { } kind)
        {
            return kind;
        }

        return obj.TryGetProperty("modality", out var mod) && mod.ValueKind == JsonValueKind.String
            ? ModelKindClassifier.NormalizeDeclared(mod.GetString())
            : null;
    }

    private static string? DeclaredFromCapabilities(JsonElement caps, string modelId)
    {
        if (caps.ValueKind == JsonValueKind.Array)
        {
            var hasVideo = false;
            var hasImage = false;
            var hasDedicatedAudio = false;
            var hasAudioOutput = false;
            var hasVisionInput = false;
            var hasChatCapability = false;
            var hasOther = false;
            foreach (var item in caps.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (item.GetString()?.Trim().ToLowerInvariant())
                {
                    case "videooutput" or "video-output" or "video_output" or "video" or "videogeneration" or "video_generation" or "video-generation":
                        hasVideo = true;
                        break;
                    case "imageoutput" or "image-output" or "image_output" or "imagegeneration" or "image_generation" or "image-generation":
                        hasImage = true;
                        break;
                    case "tts" or "stt" or "asr" or "speech" or "transcription" or "audiogeneration" or "audio_generation" or "audio-generation":
                        hasDedicatedAudio = true;
                        break;
                    case "audiooutput" or "audio-output" or "audio_output":
                        hasAudioOutput = true;
                        break;
                    case "vision" or "visioninput" or "vision_input" or "vision-input" or "imageinput" or "image_input" or "image-input":
                        hasVisionInput = true;
                        break;
                    case "reasoning" or "functioncalling" or "function_calling" or "function-calling" or "tools" or "tool_call"
                        or "structuredoutput" or "structured_output" or "structured-output"
                        or "promptcache" or "prompt_cache" or "prompt-cache"
                        or "codeexecution" or "code_execution" or "code-execution":
                        hasChatCapability = true;
                        break;
                    case "embedding" or "embeddings" or "rerank":
                        hasOther = true;
                        break;
                }
            }

            return ResolveCapabilityKind(hasVideo, hasImage, hasDedicatedAudio, hasAudioOutput, hasVisionInput, hasChatCapability, hasOther, modelId);
        }

        if (caps.ValueKind == JsonValueKind.Object)
        {
            var hasVideo = AnyBoolTrue(caps, "videoOutput", "video_output", "videoGeneration", "video_generation");
            var hasImage = AnyBoolTrue(caps, "imageOutput", "image_output", "imageGeneration", "image_generation");
            var hasDedicatedAudio = AnyBoolTrue(caps, "tts", "stt", "asr", "transcription", "audioGeneration", "audio_generation");
            var hasAudioOutput = AnyBoolTrue(caps, "audioOutput", "audio_output");
            var hasVisionInput = AnyBoolTrue(caps, "vision", "visionInput", "vision_input", "imageInput", "image_input");
            var hasChatCapability = AnyBoolTrue(caps, "reasoning", "functionCalling", "function_calling", "tools", "tool_call", "structuredOutput", "structured_output", "promptCache", "prompt_cache");
            var hasOther = AnyBoolTrue(caps, "embedding", "embeddings", "rerank");
            return ResolveCapabilityKind(hasVideo, hasImage, hasDedicatedAudio, hasAudioOutput, hasVisionInput, hasChatCapability, hasOther, modelId);
        }

        return null;
    }

    private static string? ResolveCapabilityKind(
        bool hasVideo,
        bool hasImage,
        bool hasDedicatedAudio,
        bool hasAudioOutput,
        bool hasVisionInput,
        bool hasChatCapability,
        bool hasOther,
        string modelId)
    {
        if (hasVideo)
        {
            return ProviderKinds.Video;
        }

        var idKind = ModelKindClassifier.ClassifyById(modelId);
        if (hasImage && !hasChatCapability && idKind != ProviderKinds.Video)
        {
            return ProviderKinds.Image;
        }

        if (hasDedicatedAudio && !hasChatCapability && idKind is not (ProviderKinds.Video or ProviderKinds.Image))
        {
            return ProviderKinds.Audio;
        }

        // AudioOutput 在网关的能力枚举里同时用于「TTS 音频输出」「有声视频配音(Seedance 2.x 含 VisionInput + AudioOutput)」与「多模态语音对话」:
        // 只有在不具备对话能力、不带视觉输入(排除有声视频生成模型)、且模型 ID 未指向视频/图片模型时,才作为纯音频模型的声明;否则交由目录与 ID 启发式判定。
        if (hasAudioOutput && !hasChatCapability && !hasVisionInput && idKind is not (ProviderKinds.Video or ProviderKinds.Image))
        {
            return ProviderKinds.Audio;
        }

        if (hasOther && !hasChatCapability)
        {
            return ModelKindClassifier.Other;
        }

        return null;
    }

    private static bool AnyBoolTrue(JsonElement obj, params ReadOnlySpan<string> names)
    {
        foreach (var name in names)
        {
            if (BoolTrue(obj, name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BoolTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string>? StringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }

        return list.Count == 0 ? null : list;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>失败归类。<see cref="Key"/> 供控制端翻译,<see cref="Detail"/> 是从响应里抠出来的一句话(不是整页 HTML)。</summary>
    public sealed record Failure(string Key, int Status, string? Detail);

    /// <summary>
    /// 把一次失败的拉取变成能给用户看的东西。不要把响应体原样往外送:网关和反代出错时回的往往是整页 HTML,几 KB 的标签糊在弹窗里,
    /// 用户看不出「这是网关拦的」还是「key 错了」。能解出 JSON 里的 message 就只给它,解不出就截成一行,一看就是 HTML 的不给。
    /// 401 / 403 与 404 单独归类——它们的修法(去填 key / 检查地址里的版本段)和别的失败完全不同。
    /// </summary>
    public static Failure Classify(int status, string body)
    {
        var detail = ExtractMessage(body);
        var key = status is 401 or 403 ? ProviderErrorKeys.FetchRejected
            : status == 404 ? ProviderErrorKeys.FetchNotFound
            : ProviderErrorKeys.FetchFailed;
        return new Failure(key, status, detail);
    }

    private static string? ExtractMessage(string body)
    {
        JsonElement parsed;
        try
        {
            using var document = JsonDocument.Parse(body);
            parsed = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            var line = body.Trim().Split('\n')[0].Trim();
            return line.Length == 0 || line.StartsWith('<') ? null : Truncate(line);
        }

        if (parsed.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (parsed.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                return Truncate(error.GetString()!);
            }

            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()))
            {
                return Truncate(message.GetString()!);
            }
        }

        return parsed.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(top.GetString())
            ? Truncate(top.GetString()!)
            : null;
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }
}
