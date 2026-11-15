using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Session;

/// <summary>一次压缩要的全部输入。模型 / 档位 / 输出额度由调用方决定(<see cref="CompactBinding"/>),Compactor 只把它们原样带进请求、并记进边界消息。</summary>
public sealed record CompactRequest
{
    /// <summary>完整转录。Compactor 自己取 <see cref="CompactBoundaries.ForModel"/>,调用方不要预先切。</summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary><c>auto</c>(阈值 / 上游报超长)或 <c>manual</c>(用户 /compact)。</summary>
    public required string Trigger { get; init; }

    public string? Instructions { get; init; }

    /// <summary>压缩前的占用(上游真值优先)。只用于记录和界面展示。</summary>
    public required long PreTokens { get; init; }

    /// <summary>自动压缩 = true:续接语里要求模型接着干,别停下来问。</summary>
    public bool AutoContinue { get; init; }

    public required CompactBinding Binding { get; init; }

    public required RequestContext Context { get; init; }

    public FileToolNames FileTools { get; init; } = FileToolNames.Default;

    /// <summary>压缩后重读文件:路径 → 现读的内容(读不到 / 二进制 / 太大返回 null)。null = 不重附文件。</summary>
    public Func<string, CancellationToken, ValueTask<string?>>? ReadFile { get; init; }

    /// <summary>扩展(Skills、待办……)贡献的压缩后附件文字(每段一个独立文字块)。</summary>
    public IReadOnlyList<string> ExtraAttachments { get; init; } = [];

    /// <summary>扩展给的现成摘要(<c>before_compact</c> 的自定义摘要):有就不调模型。</summary>
    public string? CustomSummary { get; init; }
}

public abstract record CompactResult
{
    public sealed record Ok(Message Message, CompactBoundaryPart Boundary) : CompactResult;

    public sealed record Failed(AgentError Error) : CompactResult;
}

/// <summary>压缩本身的结构化错误 key(控制端按 key 翻译;<c>Message</c> 是英文诊断)。</summary>
public static class CompactionErrorKeys
{
    public const string NothingToCompact = "compaction.nothingToCompact";
    public const string EmptySummary = "compaction.emptySummary";
    public const string ModelUnavailable = "compaction.modelUnavailable";
}

/// <summary>
/// 上下文压缩(04 §4,2.x <c>compaction/compact.ts</c> + pi 的摘要模板)。<b>压缩本身通过 Llm 层调用</b>(<see cref="ILlmClient"/>,可注入假上游),
/// 不碰数据库、不发事件、不知道 Run:发请求和读文件都由调用方注入,所以会话的自动压缩和手动 <c>/compact</c> 走的是同一份代码,而且能在纯单测里跑。
/// <para>不变式:</para>
/// <list type="bullet">
/// <item>摘要请求<b>不带工具</b>、系统提示只说「你负责总结」;对话展平成文本(不是原始消息数组);</item>
/// <item>准备:取最后一个边界之后的历史;去掉思考块(签名属于别的模型);图片换成占位;剥完变空的整条去掉;</item>
/// <item>超出摘要模型的输入预算时从头部裁剪,<b>切点只能落在不含工具结果的用户消息上</b>(切在 tool_result 前面,剩下的开头就是一个没有配对 tool_call 的回执),开头若是上一次的边界(上一份摘要)<b>保留它</b>;</item>
/// <item>上游报 <c>ContextLength</c> 时从最早一侧按轮丢弃重试,最多 3 次;非 ContextLength 的失败不重试;</item>
/// <item><b>迭代摘要</b>:边界之后只有上一次的边界消息 + 新消息时,把上一份摘要(去掉清单)放进 <c>&lt;previous-summary&gt;</c>,让模型更新而不是重写;读写文件清单<b>累积</b>;</item>
/// <item>边界消息是 <c>Internal</c> 的<b>用户</b>消息(不能是助手消息,否则模型会把摘要当成自己说过的话);边界之后还没有任何真实对话时拒绝压缩(刚压过,再压只会拿摘要去摘要摘要);</item>
/// <item>压缩后附件:最近读写过的最多 5 个文件<b>从磁盘重新读</b>(总 50K token、每个 5K),读失败静默跳过(锦上添花,不能让压缩因此失败);</item>
/// <item>失败返回 <see cref="CompactResult.Failed"/>,不抛(取消除外):失败计数、熔断、界面提示由调用方决定。</item>
/// </list>
/// </summary>
public sealed class Compactor(ILlmClient llm, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>上游报 prompt 太长时最多重试几次(Claude Code 的 <c>MAX_PTL_RETRIES</c>)。</summary>
    public const int MaxPtlRetries = 3;

    /// <summary>每次重试从最早一侧丢掉的比例(按消息条数)。</summary>
    public const double PtlDropRatio = 0.2;

    /// <summary>同一个 Run 里连续自动压缩失败几次就熔断(<c>MAX_CONSECUTIVE_AUTOCOMPACT_FAILURES</c>)。</summary>
    public const int MaxConsecutiveFailures = 3;

    public const int MaxRestoredFiles = 5;
    public const int RestoredFilesBudgetTokens = 50_000;
    public const int MaxTokensPerFile = 5_000;

    public async Task<CompactResult> CompactAsync(CompactRequest request, CancellationToken ct)
    {
        var history = CompactBoundaries.ForModel(request.Messages);
        // 边界之后还没有任何真实对话 = 刚压过
        if (!history.Any(m => !m.Internal))
        {
            return new CompactResult.Failed(AgentError.Create(
                AgentErrorCode.Conflict, "Nothing to compact yet.", retryable: false, messageKey: CompactionErrorKeys.NothingToCompact));
        }

        // 迭代摘要:开头是上一次的边界 → 它的摘要是「之前的记忆」,其余是新消息
        var previous = history.Count > 0 && CompactBoundaries.BoundaryOf(history[0]) is { } boundary ? boundary : null;
        var (previousBody, previousFiles) = previous is null ? ("", FileOperations.Empty) : FileOperationTracker.Split(previous.Summary);
        var fresh = previous is null ? history : [.. history.Skip(1)];

        string summaryBody;
        if (request.CustomSummary is { Length: > 0 } custom)
        {
            summaryBody = custom;
        }
        else
        {
            var converted = PrepareForSummary(fresh);
            var budget = SummaryInputBudget(request, previousBody);
            while (converted.Count > 1 && TokenEstimator.Messages(converted) + TokenEstimator.Text(previousBody) > budget)
            {
                if (TruncateHead(converted) is not { } next)
                {
                    break;
                }

                converted = next;
            }

            string? raw = null;
            AgentError? lastError = null;
            for (var attempt = 0; attempt <= MaxPtlRetries; attempt++)
            {
                var (text, error) = await SummarizeOnceAsync(request, converted, previousBody, ct).ConfigureAwait(false);
                if (text is not null)
                {
                    raw = text;
                    break;
                }

                lastError = error;
                if (error!.Code != AgentErrorCode.ContextLength || TruncateHead(converted) is not { } next)
                {
                    break;
                }

                converted = next;
            }

            if (raw is null)
            {
                return new CompactResult.Failed(lastError ?? AgentError.Create(AgentErrorCode.Provider, "Compaction returned no summary.", retryable: true));
            }

            summaryBody = CompactionPrompts.FormatSummary(raw);
        }

        if (summaryBody.Length == 0)
        {
            return new CompactResult.Failed(AgentError.Create(
                AgentErrorCode.Provider, "Compaction returned an empty summary.", retryable: true, messageKey: CompactionErrorKeys.EmptySummary));
        }

        // 累积的读写清单:上次的 + 这次新消息里的;追加在摘要后面(由代码写,不让模型改)
        var operations = FileOperationTracker.Collect(fresh, request.FileTools, previousFiles);
        var summary = summaryBody + FileOperationTracker.Format(operations);

        var attachments = await BuildAttachmentsAsync(history, request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var texts = new List<Part> { new TextPart(CompactionPrompts.Continuation(summary, request.AutoContinue)) };
        texts.AddRange(attachments.Texts.Select(t => (Part)new TextPart(t)));
        texts.AddRange(request.ExtraAttachments.Where(t => t.Length > 0).Select(t => (Part)new TextPart(t)));

        var now = clock.GetUtcNow();
        var draft = new Message(new MessageId(ids.NewId()), Role.User, texts, now, Internal: true);
        var info = request.Binding.Info;
        var result = new CompactBoundaryPart(
            request.Trigger,
            (int)Math.Max(0, request.PreTokens),
            TokenEstimator.Messages([draft]),
            summary,
            string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim(),
            attachments.RestoredFiles.Count == 0 ? null : attachments.RestoredFiles,
            info);

        // ★ Internal 的用户消息:聊天界面只画分隔线,不当成用户气泡;仍然是 user 角色,因为摘要要以「用户给的上下文」的身份出现在模型面前——
        // 放成助手消息的话,下一轮模型会以为这些话是自己说过的,复述而不是接着干。
        var message = draft with { Parts = [result, .. texts] };
        return new CompactResult.Ok(message, result);
    }

    // ── 摘要请求 ──

    /// <summary>
    /// 摘要请求的输入还能占多少 token:压缩模型的窗口 − 这次的输出额度 − 压缩指令本身。
    /// ★ 必须封底到窗口的一半:输出额度跟随全局设置(默认 32K),压缩模型可能是个 8K / 32K 窗口的小模型,那时减法是负数,
    /// 一路裁到只剩一条消息,然后拿这一条去写「整段对话的摘要」——压缩成功、摘要却只提到最后一句话,而且一个报错都没有。
    /// </summary>
    private static int SummaryInputBudget(CompactRequest request, string previousBody)
    {
        var binding = request.Binding;
        var budget = binding.ProtocolWindow - binding.MaxOutputTokens
            - TokenEstimator.Text(CompactionPrompts.Prompt(previousBody.Length > 0, request.Instructions));
        return budget > 0 ? budget : binding.ProtocolWindow / 2;
    }

    private async Task<(string? Text, AgentError? Error)> SummarizeOnceAsync(
        CompactRequest request, IReadOnlyList<Message> history, string previousBody, CancellationToken ct)
    {
        var prompt = new Message(
            new MessageId("compact:request"),
            Role.User,
            [new TextPart(CompactionPrompts.RequestText(CompactionPrompts.Serialize(history), previousBody, request.Instructions))],
            clock.GetUtcNow());
        var binding = request.Binding;
        var llmRequest = new LlmRequest(
            CompactionPrompts.SummarySystem, [prompt], [], new GenerationOptions(binding.MaxOutputTokens, binding.Thinking));
        var text = new StringBuilder();
        try
        {
            await foreach (var ev in llm.StreamAsync(binding.Model, llmRequest, request.Context, ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case TextDelta delta:
                        text.Append(delta.Text);
                        break;
                    case StreamError { Error.Code: AgentErrorCode.Aborted }:
                        throw new OperationCanceledException(ct);
                    case StreamError error:
                        return (null, error.Error);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (null, AgentError.Create(AgentErrorCode.Provider, e.Message, retryable: true));
        }

        return (text.ToString(), null);
    }

    /// <summary>
    /// 摘要请求的输入:原始对话去掉模型用不上、又可能让请求非法的东西。图片(含工具截图)换成一句占位——摘要不需要看图,而图片是最贵的输入;
    /// 思考块去掉——那些签名是会话模型在别的轮次里生成的,而写摘要的可能根本是另一个模型;剥完变成空消息的整条去掉。
    /// 托管图(<c>ncw://</c>)的占位里带上地址:摘要是压缩后模型唯一的记忆,地址丢了它就再也点不了名。
    /// </summary>
    public static List<Message> PrepareForSummary(IEnumerable<Message> history)
    {
        var output = new List<Message>();
        foreach (var message in history)
        {
            var parts = new List<Part>();
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case ThinkingPart:
                        break;
                    case ImagePart image:
                        parts.Add(new TextPart(image.DataRef.StartsWith("ncw://", StringComparison.Ordinal) ? $"[image] {image.DataRef}" : "[image]"));
                        break;
                    case ToolResultPart { Output.Images.Count: > 0 } result:
                        parts.Add(result with { Output = result.Output with { Images = null } });
                        break;
                    default:
                        parts.Add(part);
                        break;
                }
            }

            if (parts.Count > 0)
            {
                output.Add(message with { Parts = parts });
            }
        }

        return output;
    }

    /// <summary>
    /// 从最早一侧丢掉约 <see cref="PtlDropRatio"/> 的消息,切在「一轮的开头」。★ 切点必须是一条不含 tool_result 的 user 消息;
    /// ★ 开头若是上一次的边界消息(上一份摘要)保留它,只丢它后面的。返回 null = 已经丢不动了。
    /// </summary>
    public static List<Message>? TruncateHead(IReadOnlyList<Message> messages)
    {
        var keepHead = messages.Count > 0 && messages[0].Parts.Any(p => p is CompactBoundaryPart) ? 1 : 0;
        var body = messages.Count - keepHead;
        if (body <= 1)
        {
            return null;
        }

        var target = keepHead + Math.Max(1, (int)Math.Floor(body * PtlDropRatio));
        for (var i = target; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role == Role.User && !m.Parts.Any(p => p is ToolResultPart))
            {
                return [.. messages.Take(keepHead), .. messages.Skip(i)];
            }
        }

        return null;
    }

    // ── 压缩后附件 ──

    private sealed record Attachments(IReadOnlyList<string> Texts, IReadOnlyList<string> RestoredFiles);

    /// <summary>
    /// 摘要写的是「做过什么」,不是「文件现在长什么样」。压完之后模型手里如果只剩摘要,它的第一个动作几乎一定是把刚才在改的那几个文件重新读一遍——
    /// 白白多一轮往返,而且在那之前它会基于摘要里的旧片段去改,old_string 对不上。所以压完立刻把最近碰过的文件<b>从磁盘重新读一遍</b>(不从转录里抄:转录里是当时的快照,之后可能已被改过)。
    /// </summary>
    private static async Task<Attachments> BuildAttachmentsAsync(IReadOnlyList<Message> history, CompactRequest request, CancellationToken ct)
    {
        var texts = new List<string>();
        var restored = new List<string>();
        if (request.ReadFile is not { } read)
        {
            return new Attachments(texts, restored);
        }

        var budget = RestoredFilesBudgetTokens;
        foreach (var path in FileOperationTracker.RecentPaths(history, request.FileTools))
        {
            if (restored.Count >= MaxRestoredFiles || budget <= 0 || ct.IsCancellationRequested)
            {
                break;
            }

            string? content;
            try
            {
                content = await read(path, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                content = null; // 重附失败不能让整次压缩失败
            }

            if (content is null)
            {
                continue;
            }

            var body = TruncateToTokens(content, Math.Min(MaxTokensPerFile, budget));
            budget -= TokenEstimator.Text(body);
            restored.Add(path);
            texts.Add(
                $"<system-reminder>\nCalled the Read tool with the following input: {{\"file_path\":{JsonString(path)}}}\n"
                + $"Result of calling the Read tool (re-read from disk after compaction; this is the current content):\n{body}\n</system-reminder>");
        }

        return new Attachments(texts, restored);
    }

    private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    /// <summary>按估算 token 截断到上限,截断处留一句说明——让模型知道要看全文得自己读。二分找最长前缀(估算对 CJK 和拉丁字符的系数不同,不能直接按字符比例切)。</summary>
    public static string TruncateToTokens(string text, int maxTokens)
    {
        if (TokenEstimate.OfText(text) <= maxTokens)
        {
            return text;
        }

        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (TokenEstimate.OfText(text[..mid]) <= maxTokens)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (lo > 0 && lo < text.Length && char.IsHighSurrogate(text[lo - 1]))
        {
            lo--;
        }

        return $"{text[..lo]}\n\n[... truncated for context. Read the file again to see the rest of it.]";
    }

    /// <summary>
    /// 读一个可重附的文本文件(2.x <c>readAttachableFile</c>):不存在 / 目录 / 超过 1 MB / 二进制都返回 null。
    /// 路径解析走环境的路径守卫(相对路径接在工作区根后面,绝对路径原样);**只经 <see cref="IExecutionEnvironment"/>**。
    /// 1 MB 的闸看起来多余(反正会截到 5K token),但截断发生在读完之后:一个模型读过前 200 行的 80 MB 日志,不设这道闸就会在压缩时被整个读进内存再扔掉 99.9%。
    /// </summary>
    public static async ValueTask<string?> ReadAttachableFileAsync(IExecutionEnvironment environment, EnvPath root, string path, CancellationToken ct)
    {
        const long maxBytes = 1024 * 1024;
        const int sniffBytes = 4096;
        try
        {
            var resolved = await environment.Paths.ResolveAsync(root, path, ct).ConfigureAwait(false);
            var info = await environment.Fs.StatAsync(resolved.Absolute, ct).ConfigureAwait(false);
            if (!info.IsFile || info.Size > maxBytes)
            {
                return null;
            }

            var head = await environment.Fs.ReadHeadAsync(resolved.Absolute, sniffBytes, ct).ConfigureAwait(false);
            if (Array.IndexOf(head, (byte)0) >= 0)
            {
                return null; // 二进制
            }

            return await environment.Fs.ReadTextAsync(resolved.Absolute, maxBytes, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is not (EnvironmentErrorCode.Cancelled or EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.StaleGeneration))
        {
            return null;
        }
    }
}
