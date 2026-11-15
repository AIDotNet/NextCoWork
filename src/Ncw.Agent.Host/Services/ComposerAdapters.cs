using System.Text;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel;
using Ncw.Agent.Llm;
using Ncw.Agent.Services.Composer;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Ids;

namespace Ncw.Agent.Host.Services;

/// <summary>补全的数据来源(<see cref="IComposerSource"/>):设置、会话仓库、目标服务。</summary>
internal sealed class HostComposerSource(ConversationService conversation) : IComposerSource
{
    public DeviceSettings Settings() => conversation.DeviceSettings.Current();

    public ComposerSession? Session(string sessionId) =>
        conversation.Sessions.Get(sessionId) is { } s ? new ComposerSession(s.Id, s.WorkspaceId, s.ParentSessionId, s.Mode, s.SkillSourceSessionId is not null) : null;

    public bool HasActiveGoal(string sessionId) => conversation.Planning.Goals.Store.Get(sessionId) is not null;

    public IReadOnlyList<ComposerMessage> RecentSessionPrompts(string sessionId, int limit) =>
        [.. conversation.Sessions.RecentPrompts(sessionId, limit).Select(static m => new ComposerMessage(VisibleText(m), m.CreatedAt))];

    public ComposerMessage? LastVisibleAssistant(string sessionId) =>
        conversation.Sessions.LastVisibleAssistant(sessionId) is { } m ? new ComposerMessage(VisibleText(m), m.CreatedAt) : null;

    public IReadOnlyList<string> RecentWorkspacePrompts(string workspaceId) => conversation.Sessions.RecentWorkspacePrompts(workspaceId);

    /// <summary>消息里用户 / 助手看得见的文本(<c>text</c> 块拼起来;工具调用、思考不算)。</summary>
    private static string VisibleText(MessageRecord message)
    {
        var text = new StringBuilder();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message.PartsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var part in doc.RootElement.EnumerateArray())
                {
                    if (part.ValueKind == System.Text.Json.JsonValueKind.Object && part.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && part.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        text.Append(t.GetString());
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 坏 JSON 的消息没有可见文本
        }

        return text.ToString();
    }
}

/// <summary>
/// 补全的模型调用(<see cref="IComposerModel"/>):经路由(和对话同一套供应商 / 凭据 / 计价,用量记在这条会话的费用明细里)发一个无工具、短输出、能关思考就关的独立请求。
/// 模型不存在 / 失败 / 被取消 / 想调工具 / 回答过长 / 没有正常结束一律是空串:补全是便利不是功能。不换家:跟随输入框此刻的模型,或用户在设置里点名的那个。
/// </summary>
internal sealed class HostComposerModel(ConversationService conversation, TimeProvider clock) : IComposerModel
{
    private const int ResponseLimit = 1_000;

    public Task<string> SuggestAsync(SuggestInput input, CancellationToken ct) =>
        RunAsync(input.Model, input.WorkspaceId, input.SessionId, "suggest", Prompts.Suggestion, Prompts.FormatSuggestion(input.Context), maxOutput: 128, stop: "</suggestion>", ComposerText.ParseSuggestion, ct);

    public Task<string> CompleteAsync(InlineInput input, CancellationToken ct) =>
        RunAsync(input.Model, input.WorkspaceId, input.SessionId ?? "", "complete", Prompts.Inline, Prompts.FormatInline(input), maxOutput: 64, stop: "</completion>", raw => Prompts.ParseInline(raw, input.Draft), ct);

    private async Task<string> RunAsync(
        ModelPick pick, string workspaceId, string sessionId, string purpose, string system, string user, int maxOutput, string stop, Func<string, string> parse, CancellationToken ct)
    {
        var model = new ModelRef(pick.ProviderId, pick.Model);
        if (conversation.Models.Resolve(model) is null)
        {
            return "";
        }

        var runId = $"{purpose}_{Ulid.New(clock)}";
        var message = new Message(new MessageId(runId), Role.User, [new TextPart(user)], clock.GetUtcNow());
        var request = new LlmRequest(system, [message], [], new GenerationOptions(maxOutput, ThinkingLevel.Off, StopSequences: [stop]));
        var result = new StringBuilder();
        var complete = false;
        try
        {
            await foreach (var ev in conversation.Models.Client.StreamAsync(model, request, new RequestContext(workspaceId, runId, sessionId.Length == 0 ? null : sessionId), ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case StreamError:
                        return "";
                    case ToolCallStart:
                        return "";
                    case TextDelta delta:
                        result.Append(delta.Text);
                        if (result.Length > ResponseLimit)
                        {
                            return "";
                        }

                        break;
                    case MessageEnd end:
                        complete = end.Stop is StopReason.EndTurn or StopReason.StopSequence;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return "";
        }

        return complete && !ct.IsCancellationRequested ? parse(result.ToString()) : "";
    }
}
