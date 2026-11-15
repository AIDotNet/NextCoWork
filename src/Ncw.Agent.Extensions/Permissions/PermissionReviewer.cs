using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Extensions.Permissions;

public enum ReviewResult
{
    Allow,
    Deny,

    /// <summary>任何网络 / 解析 / 模型错误、没配审核器、含糊的回答:回退人工审批,避免「审核器挂了却放行」。</summary>
    Unknown,
}

public sealed record ReviewRequest(RunId RunId, string SessionId, string WorkspaceId, ModelRef? Model, string UserTask, string ToolName, JsonElement Input);

/// <summary>「为我批准」(<c>auto</c> 档 + 破坏性工具)的 AI 审核接入口。</summary>
public interface IPermissionReviewer
{
    ValueTask<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct);
}

/// <summary>
/// 默认审核器(2.x <c>approval.ts</c> 的 <c>reviewSensitiveOperation</c>):一个无工具、128 token 的独立请求,不继承当前 Agent 的权限,也不能再调用工具。
/// 任何失败都返回 <see cref="ReviewResult.Unknown"/>,由调用方回退到人工审批。
/// </summary>
public sealed partial class LlmPermissionReviewer(ILlmClient llm, ILoopDiagnostics? log = null, ThinkingLevel thinking = ThinkingLevel.Off) : IPermissionReviewer
{
    public const int MaxOutputTokens = 128;

    private readonly ILoopDiagnostics _log = log ?? NullLoopDiagnostics.Instance;

    private const string System = "Be conservative. Deny destructive, irreversible, credential-related, or ambiguous actions.";

    internal static string BuildPrompt(ReviewRequest request)
    {
        var task = Clip(request.UserTask, 8000);
        var input = Clip(request.Input.ValueKind == JsonValueKind.Undefined ? "{}" : request.Input.GetRawText(), 12000);
        return string.Join('\n',
            "You are a safety reviewer for an AI computer-use agent.",
            "Decide whether the proposed tool call is safe and consistent with the user task.",
            "Reply with exactly one JSON object and no markdown: {\"decision\":\"allow\"} or {\"decision\":\"deny\"}.",
            $"User task: {task}",
            $"Tool: {request.ToolName}",
            $"Input: {input}");
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    public async ValueTask<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        if (request.Model is not { Alias: { } alias } model || string.IsNullOrWhiteSpace(alias))
        {
            return ReviewResult.Unknown;
        }

        var message = new Message(new MessageId($"{request.RunId}:permission-review"), Role.User, [new TextPart(BuildPrompt(request))], DateTimeOffset.UnixEpoch);
        // 审核请求要短要快,能不思考就不思考。模型层的思考适配器负责把「关不掉」的模型降到最低可用档(2.x 的 auxiliaryThinkingLevel),这里不硬编码。
        var llmRequest = new LlmRequest(System, [message], [], new GenerationOptions(MaxOutputTokens, thinking));
        var text = new StringBuilder();
        try
        {
            await foreach (var ev in llm.StreamAsync(model, llmRequest, new RequestContext(request.WorkspaceId, $"{request.RunId}:permission-review", request.SessionId), ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case TextDelta delta:
                        text.Append(delta.Text);
                        break;
                    case StreamError:
                        return ReviewResult.Unknown;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.Warn($"[permission-review] model request failed for {alias}", e);
            return ReviewResult.Unknown;
        }

        var result = Parse(text.ToString());
        if (result == ReviewResult.Unknown)
        {
            _log.Warn($"[permission-review] model returned an unrecognized decision for {alias}");
        }

        return result;
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Fenced();

    [GeneratedRegex(@"\{\s*[""']decision[""']\s*:\s*[""'](?:allow|deny)[""']\s*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Embedded();

    [GeneratedRegex(@"\bdecision\s*[:=]\s*[""']?allow\b|^\s*(?:allow|allowed|approve|approved|允许|同意)\s*[.!。]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex LooseAllow();

    [GeneratedRegex(@"\bdecision\s*[:=]\s*[""']?deny\b|^\s*(?:deny|denied|拒绝|禁止)\s*[.!。]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex LooseDeny();

    /// <summary>
    /// 解析审核器的回答。兼容模型常见的 <c>```json</c> 包裹、前后解释文字,以及中文「允许 / 拒绝」。
    /// 只接受明确的 <c>decision</c> 字段或整句明确答案;含糊内容回退人工审批。
    /// </summary>
    public static ReviewResult Parse(string text)
    {
        try
        {
            var normalized = text.Trim();
            var candidates = new List<string> { normalized };
            if (Fenced().Match(normalized) is { Success: true } fenced)
            {
                candidates.Add(fenced.Groups[1].Value.Trim());
            }

            if (Embedded().Match(normalized) is { Success: true } embedded)
            {
                candidates.Add(embedded.Value);
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    using var doc = JsonDocument.Parse(candidate);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("decision", out var decision)
                        && decision.ValueKind == JsonValueKind.String)
                    {
                        switch (decision.GetString())
                        {
                            case "allow":
                                return ReviewResult.Allow;
                            case "deny":
                                return ReviewResult.Deny;
                        }
                    }
                }
                catch (JsonException)
                {
                    // 试下一种写法
                }
            }

            // 3.0 收紧:整句明确答案才算(2.x 的正则没有锚定,「I would not allow.」会被读成允许);两种都沾边 = 含糊,回退人工。
            var allow = LooseAllow().IsMatch(normalized);
            var deny = LooseDeny().IsMatch(normalized);
            return allow == deny ? ReviewResult.Unknown : allow ? ReviewResult.Allow : ReviewResult.Deny;
        }
        catch (RegexMatchTimeoutException)
        {
            return ReviewResult.Unknown;
        }
    }
}
