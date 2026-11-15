using System.Buffers;
using System.Text.Json;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Extensions.Interactions;

/// <summary>
/// <c>AskUserQuestion</c>(2.x <c>interaction.ts</c>):向用户提 1–4 道题(每题最多 8 个选项,可多选,可自由作答),经交互闸门挂起直到有人回答(没有超时;多个控制端先到先得)。
/// <list type="bullet">
/// <item>只有顶层 Run 能问人:子代理的工具快照里没有它(<see cref="IsEnabled"/>,与 <c>SubagentExtension.InteractiveToolIds</c> 对接),就算漏过去,子代理的交互作用域
/// <c>depth &gt; 0</c> 发起即拒绝(<see cref="RunInteractions.CanInteract"/>)——三道闸之二、之三。</item>
/// <item>既没选项又不许自由作答的题在界面上无从回答:在挂起之前就失败(否则用户看到点不动的题,模型在等永远不会来的回答)。<see cref="AskUser.ValidateDraft"/> 在闸门里还有一道。</item>
/// <item>用户关掉了框 = 工具错误「不要自己假设答案」;回答按 <c>header</c> 和题面一起回给模型,不只按下标。</item>
/// <item><c>ReadOnly</c>:它不改任何东西,也不是「执行」类审批的对象(工具权限和提问是两件事)。</item>
/// </list>
/// 工具所需的一切只有「按 RunId 找 Run 的交互作用域」,所以构造参数就是 <see cref="PermissionRuns"/>(顶层 Run 的权限状态里带着它)。
/// </summary>
public sealed class AskUserQuestionTool(PermissionRuns runs) : BuiltinTool(
    "AskUserQuestion",
    "Ask the user one or more focused questions when their answers are needed to continue. "
    + "Ask everything you need for the current decision in a single call (at most 4 questions), each question with its own options; "
    + "wait for the actual answers before proceeding. A question may offer up to 8 options (optionally multi-select) and may also allow a free-form answer. "
    + "Do not use this tool for tool execution permission, which is handled automatically.",
    S.Build(S.Object(
        ["questions"],
        ("questions", S.Array(
            "1 to 4 questions",
            S.Object(
                ["header", "question"],
                ("header", S.String("Short label for this question, at most a few words", minLength: 1, maxLength: 24)),
                ("question", S.String("The full question", minLength: 1, maxLength: 8000)),
                ("options", S.Array(
                    "Choices to offer (at most 8). Omit or leave empty to ask for a free-form answer only",
                    S.Object(
                        ["label"],
                        ("label", S.String("The choice, short", minLength: 1, maxLength: 1000)),
                        ("description", S.String("What choosing this means", maxLength: 1000))),
                    maxItems: AskUser.MaxOptions)),
                ("multiSelect", S.Boolean("Allow more than one option to be selected. Default false")),
                ("allowFreeform", S.Boolean("Also let the user type an answer of their own. Default true"))),
            minItems: 1,
            maxItems: AskUser.MaxQuestions)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Ask the user questions and wait for their answers",
    ["Use AskUserQuestion only when you cannot continue without the user's decision; do not ask about things you can look up or decide yourself."])
{
    public override bool IsEnabled(ToolContext ctx) => ctx.Depth == 0;

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var questions = new List<AskUserQuestion>();
        var index = 0;
        foreach (var q in args.Arr("questions"))
        {
            index++;
            var header = (q.Str("header") ?? "").Trim();
            var text = (q.Str("question") ?? "").Trim();
            if (header.Length == 0 || text.Length == 0)
            {
                throw Fail($"Question {index} needs a non-empty header and question.");
            }

            var options = new List<AskUserOption>();
            foreach (var o in q.Arr("options"))
            {
                var label = (o.Str("label") ?? "").Trim();
                if (label.Length == 0)
                {
                    throw Fail($"Question {index} has an option with an empty label.");
                }

                var description = o.Str("description") is { } d && d.Trim() is { Length: > 0 } trimmed ? trimmed : null;
                options.Add(new AskUserOption(label, description));
            }

            var allowFreeform = q.Bool("allowFreeform") ?? true;
            // 既没选项又不许自由作答的题无从回答:挂起之前就失败
            if (options.Count == 0 && !allowFreeform)
            {
                throw Fail($"Question {index} has no options and does not allow a freeform answer.");
            }

            questions.Add(new AskUserQuestion(header, text, options, q.Bool("multiSelect") ?? false, allowFreeform));
        }

        var draft = new AskUser(questions);
        if (draft.ValidateDraft() is { } invalid)
        {
            throw Fail(invalid);
        }

        var interactions = runs.Get(ctx.RunId)?.Interactions;
        if (interactions is not { CanInteract: true })
        {
            throw Fail("User interaction is unavailable here: only the top-level run can ask the user questions.");
        }

        InteractionResponse response;
        try
        {
            response = await interactions.RequestAsync(draft, ct).ConfigureAwait(false);
        }
        catch (InteractionRefusedException)
        {
            throw Fail("User interaction is unavailable here: only the top-level run can ask the user questions.");
        }

        if (response is not AskUserResponse reply)
        {
            throw Fail("Unexpected interaction response.");
        }

        if (reply.Answers is not { } answers)
        {
            throw Fail("The user dismissed these questions. Do not assume an answer.");
        }

        // 按 header + 题面回给模型,不只按下标:模型对着自己写的 header 对答案比对着数字可靠
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("answers");
            for (var i = 0; i < questions.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("header", questions[i].Header);
                w.WriteString("question", questions[i].Question);
                w.WriteStartArray("answer");
                foreach (var a in i < answers.Count ? answers[i] : [])
                {
                    w.WriteStringValue(a);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return ToolResult.Text(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan), Details(ctx.Runtime, "askUser", w =>
        {
            w.WriteNumber("questionCount", questions.Count);
        }));
    }
}
