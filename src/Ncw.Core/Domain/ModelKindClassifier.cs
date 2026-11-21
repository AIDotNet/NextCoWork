using System.Text.RegularExpressions;

namespace Ncw.Core.Domain;

/// <summary>
/// 模型类型归类(纯函数,Agent 拉取模型列表过滤与托管供应商分流共用,AGENTS.md §3 规则 3)。
/// 返回值:<see cref="ProviderKinds.Text"/> / <see cref="ProviderKinds.Image"/> / <see cref="ProviderKinds.Video"/> /
/// <see cref="ProviderKinds.Audio"/>,或 <see cref="Other"/>(嵌入、重排、审核等非生成类模型)。
/// <para>
/// 判定优先级:
/// 1. 上游 <c>/models</c> 显式声明的类型 / 能力 / 输出模态(<paramref name="declaredKind"/>);
/// 2. 模型目录(models.dev)的输入 / 输出模态;
/// 3. 保守的模型 ID 启发式(顺序:非生成工具模型 → 视频 → 图片 → 音频 → 默认文本)。
/// 视频先于图片判定:<c>grok-imagine-video</c>、<c>doubao-seedance</c> 归视频而不误落进图片。
/// </para>
/// </summary>
public static partial class ModelKindClassifier
{
    /// <summary>嵌入、重排、内容审核等非对话 / 非媒体的工具模型(四种供应商拉取时都过滤掉)。</summary>
    public const string Other = "other";

    public static string Classify(
        string modelId,
        string? declaredKind = null,
        IReadOnlyList<string>? catalogInputModalities = null,
        IReadOnlyList<string>? catalogOutputModalities = null)
    {
        if (NormalizeDeclared(declaredKind) is { } declared)
        {
            return declared;
        }

        if (FromModalities(catalogInputModalities, catalogOutputModalities, modelId) is { } fromCatalog)
        {
            return fromCatalog;
        }

        return ClassifyById(modelId);
    }

    /// <summary>根据模型目录或上游响应里的输入 / 输出模态数组判定类型;数组为空(目录未声明)返回 null 交由 ID 启发式兜底。</summary>
    public static string? FromModalities(IReadOnlyList<string>? inputModalities, IReadOnlyList<string>? outputModalities, string? modelId = null)
    {
        if (outputModalities is { Count: > 0 })
        {
            var hasTextOutput = ContainsModality(outputModalities, "text");
            if (ContainsModality(outputModalities, "video"))
            {
                // 全模态对话模型(如 gemini-omni-1.2-flash:output=["text","video"])仍是文本对话模型;纯视频生成才归 video
                return hasTextOutput && ClassifyById(modelId ?? string.Empty) != ProviderKinds.Video
                    ? ProviderKinds.Text
                    : ProviderKinds.Video;
            }

            if (ContainsModality(outputModalities, "image"))
            {
                return ProviderKinds.Image;
            }

            if (ContainsModality(outputModalities, "audio") || ContainsModality(outputModalities, "speech"))
            {
                // 全模态对话模型(如 qwen3-omni-flash:output=["text","audio"])同时输出文本与语音;除非模型 ID 明示为专用语音/实时/音乐模型,否则归文本
                return hasTextOutput && ClassifyById(modelId ?? string.Empty) != ProviderKinds.Audio
                    ? ProviderKinds.Text
                    : ProviderKinds.Audio;
            }

            // 纯语音转写(STT:输入只有 audio、输出是 text):归音频模型而不是文本聊天模型。
            if (inputModalities is { Count: > 0 }
                && ContainsModality(inputModalities, "audio")
                && !ContainsModality(inputModalities, "text"))
            {
                return ProviderKinds.Audio;
            }

            if (hasTextOutput)
            {
                return ProviderKinds.Text;
            }
        }
        else if (inputModalities is { Count: > 0 }
            && ContainsModality(inputModalities, "audio")
            && !ContainsModality(inputModalities, "text"))
        {
            return ProviderKinds.Audio;
        }

        return null;
    }

    /// <summary>把上游 <c>/models</c> 行里声明的原始类型 / 模态字符串规范化;认不出返回 null。</summary>
    public static string? NormalizeDeclared(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant();
        // OpenRouter 风格 "text->image" / "text+image->text" / "text->video" / "text->audio"
        var arrow = value.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            var inputPart = value[..arrow];
            var outputPart = value[(arrow + 2)..];
            if (outputPart.Contains("video", StringComparison.Ordinal))
            {
                return ProviderKinds.Video;
            }

            if (outputPart.Contains("image", StringComparison.Ordinal))
            {
                return ProviderKinds.Image;
            }

            if (outputPart.Contains("audio", StringComparison.Ordinal) || outputPart.Contains("speech", StringComparison.Ordinal))
            {
                return ProviderKinds.Audio;
            }

            if (inputPart.Contains("audio", StringComparison.Ordinal) && !inputPart.Contains("text", StringComparison.Ordinal))
            {
                return ProviderKinds.Audio;
            }

            if (outputPart.Contains("text", StringComparison.Ordinal))
            {
                return ProviderKinds.Text;
            }
        }

        return value switch
        {
            "text" or "chat" or "llm" or "completion" or "completions" or "language" or "textgeneration" or "text-generation" or "text_generation" => ProviderKinds.Text,
            "image" or "images" or "imagegeneration" or "image-generation" or "image_generation" or "imageedit" or "image-edit" or "image_edit" or "text-to-image" or "text_to_image" or "t2i" or "i2i" or "vision-generation" => ProviderKinds.Image,
            "video" or "videos" or "videogeneration" or "video-generation" or "video_generation" or "text-to-video" or "text_to_video" or "image-to-video" or "image_to_video" or "t2v" or "i2v" or "v2v" => ProviderKinds.Video,
            "audio" or "speech" or "tts" or "stt" or "asr" or "transcription" or "transcriptions" or "voice" or "music" or "audiogeneration" or "audio-generation" or "audio_generation" or "text-to-speech" or "speech-to-text" or "volcenginetts" or "volcengine-tts" or "volcengine_tts" => ProviderKinds.Audio,
            "embedding" or "embeddings" or "rerank" or "reranker" or "moderation" or "moderations" or "websearch" or "web-search" or "web_search" or "other" => Other,
            _ => null,
        };
    }

    /// <summary>按模型 ID 的保守启发式归类。认不出特殊媒体模态时默认归 <see cref="ProviderKinds.Text"/>。</summary>
    public static string ClassifyById(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return ProviderKinds.Text;
        }

        var id = modelId.Trim().ToLowerInvariant();
        if (UtilityPattern().IsMatch(id))
        {
            return Other;
        }

        if (VideoPattern().IsMatch(id))
        {
            return ProviderKinds.Video;
        }

        if (ImagePattern().IsMatch(id))
        {
            return ProviderKinds.Image;
        }

        if (AudioPattern().IsMatch(id))
        {
            return ProviderKinds.Audio;
        }

        return ProviderKinds.Text;
    }

    private static bool ContainsModality(IReadOnlyList<string> modalities, string expected)
    {
        for (var i = 0; i < modalities.Count; i++)
        {
            if (string.Equals(modalities[i], expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // 嵌入 / 重排 / 内容审核等工具模型
    [GeneratedRegex(@"(?:^|[/_:.-])(embeddings?|rerank(?:er)?|moderation|omni-moderation|text-moderation|bge-(?:m3|large|base|small)|jina-embeddings?|voyage-(?:code|large|lite|multilingual|3|2))(?:$|[/_:.-])", RegexOptions.CultureInvariant)]
    private static partial Regex UtilityPattern();

    // 视频生成模型(先于图片判定,避免 grok-imagine-video、kling-video、seedance 误落到图片)
    [GeneratedRegex(@"(?:^|[/_:.-])(seedance|sora|veo|kling|hailuo|cogvideox?|viduq?\d*|runway|luma|nova[-_]?reel|pixverse|pika|mochi|ltx[-_]?video|step[-_]?video|hunyuan[-_]?video|jimeng[-_]?video|video[-_]?01|video[-_]?gen(?:eration)?|t2v|i2v|v2v|kf2v)(?:$|[/_:.-]|\d)|(?:^|[/_:.-])ray[-_]?\d|(?:^|[/_:.-])wanx?\d*(?:\.\d+)?[-_]?(?:t2v|i2v|v2v|kf2v|video)|(?:^|[/_:.-])video(?:$|[/_:.-])", RegexOptions.CultureInvariant)]
    private static partial Regex VideoPattern();

    // 图片生成模型(刻意不含 seedance)
    [GeneratedRegex(@"(?:^|[/_:.-])(seedream|dall[-_]?e|dalle|gpt[-_]?image|chatgpt[-_]?image|imagen|flux|qwen[-_]?image|z[-_]?image|imagegen|image[-_]?gen(?:eration)?|imagine[-_]?image|wanx\d*(?:\.\d+)?(?:[-_]?(?:t2i|v\d|style|sketch|edit))?|kolors|sdxl|stable[-_]?diffusion|sd3(?:\.\d)?|ideogram|midjourney|recraft|pixart|playground[-_]?v\d|cogview|hunyuan[-_]?(?:image|dit)|nano[-_]?banana|t2i|i2i)(?:$|[/_:.-]|\d)|(?:^|[/_:.-])image(?:$|[/_:.-])", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    // 音频模型(TTS / STT / 语音对话 / 实时音频 / 音乐生成)
    [GeneratedRegex(@"(?:^|[/_:.-])(whisper|tts|stt|asr|transcribe|transcription|cosyvoice|fish[-_]?speech|paraformer|sensevoice|funasr|kokoro|chatterbox|f5[-_]?tts|bark|eleven[-_]?|playht|speech[-_]?\d|minimax[-_]?speech|qwen[-_]?(?:tts|asr)|doubao[-_]?(?:tts|asr|voice)|seed[-_]?(?:tts|asr)|volc[-_]?(?:tts|asr)|glm[-_]?4[-_]?voice|gpt[-_]?(?:4o[-_]?(?:mini[-_]?)?)?(?:audio|realtime)|voxtral|suno|udio|lyria)(?:$|[/_:.-]|\d)|(?:^|[/_:.-])(audio|speech|voice|music)(?:$|[/_:.-])", RegexOptions.CultureInvariant)]
    private static partial Regex AudioPattern();
}
