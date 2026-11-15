using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ncw.Agent.Parity.Tests.Compare;

/// <summary>
/// 已登记的预期差异。每条规则:一个 id、引用的文档条目(<see cref="DocRef"/>,有测试核对引文真的在文档里)、一句原因、一个只作用于具体条目形状的改写。
/// 规则按声明顺序依次应用;<c>L</c> = 2.x 一侧(录制),<c>R</c> = C# 一侧(回放)。
/// 新发现的、文档里原先没有的差异先登记在 docs/parity.md「回放对照」一节,再在这里放行。
/// </summary>
public static partial class Rules
{
    private const string Parity = "docs/parity.md";
    private const string Loop = "docs/design/08-agent-runtime/02-loop.md";

    private const string Interrupted = "[interrupted:";
    private const string UpstreamIncomplete = "[not executed: the upstream response did not complete successfully]";
    private const string OutputLimitResult =
        "[not executed: the response reached the output limit, so the arguments of this tool call may have been truncated. Re-issue the tool call.]";

    public static IReadOnlyList<Rule> All { get; } =
    [
        // ───────────── 事件(循环层 / 会话层的事件词汇) ─────────────
        DropR("EV-LIFECYCLE", Section.Events,
            new(Loop, "第一个事件是 `run_start`、最后一个是 `run_end`,各恰好一个;`turn_start` / `turn_end` 严格成对嵌套"),
            "3.0 的循环层 / 会话层多发 run_start、turn_start、turn_end、run_settled;2.x 的事件只有 stream / message_commit / tool_* / context_* / run_end。",
            e => E(e) is "run_start" or "turn_start" or "turn_end" or "run_settled"),

        DropR("EV-MESSAGE-START", Section.Events,
            new(Loop, "每条消息都有 start / end,包括用户、引导、工具结果消息"),
            "3.0 每条消息都有 message_start / message_end;2.x 只在提交时发一个 message_commit(助手消息的开头折在第一个 stream 增量里)。",
            e => E(e) == "message_start"),

        DropL("EV-STREAM-ERROR", Section.Events,
            new(Parity, "2.x 的 `stream` 事件逐个原样转发"),
            "2.x 把模型层的 error 事件当 stream 增量转发;3.0 的事件是消息级的,流错误由循环层处理后体现在 provider_retry / 带 ErrorPart 的消息 / run_end 上。",
            e => E(e) == "stream_error"),

        DropR("EV-DISCARD", Section.Events,
            new(Parity, "丢弃时新增 `message_discard` 事件,让客户端丢掉那份增量(2.x 靠下一个 `message_start` 覆盖)"),
            "3.0 新增 message_discard。",
            e => E(e) == "discard"),

        new("EV-TOOL-START-FOR-EVERY-CALL", Section.Events,
            new(Loop, "**助手消息里的每个调用都有**(包括 JSON 无效、工具不存在、被拒绝、被中止的),预检时按调用顺序发"),
            "2.x 对 JSON 无效 / 未知工具 / 补「未执行」结果的调用只发 tool_end,3.0 每个调用都先发 tool_start。",
            (l, r, _, _) =>
            {
                var started = l.Where(n => E(n) == "tool_start").Select(n => Str(n, "callId")).ToHashSet();
                return r.RemoveAll(n => E(n) == "tool_start" && !started.Contains(Str(n, "callId")));
            }),

        // ───────────── 错误 key / 工具结果文字(发给模型的英文,随实现改写的那几句) ─────────────
        TextRule("ERROR-KEYS", Section.Events | Section.Rows,
            new(Loop, "`MessageKey = loop.outputLimit`,2.x 的 `agent.error.outputLimit`"),
            "错误的 MessageKey 换了命名空间:2.x 的 agent.error.<x> 在 3.0 是 loop.<x>(循环层)/ llm.<x>(模型层),后缀同名。",
            new KeyPrefix(), left: "agent.error.", right: ["loop.", "llm."]),

        TextRule("TEXT-TOOL-EXCEPTION", Section.Events | Section.Rows | Section.Requests,
            new(Parity, "工具抛异常的结果文字 `Tool execution failed: <类型名>: <消息>` 里的类型名随平台"),
            "工具抛异常时,结果文字带的是异常的类型名:JS 是 Error,.NET 是具体的异常类型。",
            new Regex(@"^Tool execution failed: [A-Za-z0-9_.]+: ", RegexOptions.Compiled), "Tool execution failed: <exception>: "),

        TextRule("TEXT-UNKNOWN-TOOL", Section.Events | Section.Rows | Section.Requests,
            new(Parity, "未知工具的结果文字在 2.x 的句子后面追加可用工具名列表"),
            "3.0 在「没有这个工具」的提示后面列出本回合可用的工具名,帮模型改正。",
            new Regex(@"(Pick one from the list of available tools)(: [^\n]*?)?\.$", RegexOptions.Compiled), "$1."),

        TextRule("TEXT-INVALID-ARGUMENTS", Section.Events | Section.Rows | Section.Requests,
            new(Parity, "参数 JSON 无效的结果文字改写成「请重发」并带解析器的原因"),
            "两边都把模型发来的参数原文带回;提示句和 JSON 解析器给的原因文字不同(原因随平台)。",
            new InvalidArguments()),

        TextRule("TEXT-TRUNCATION-NOTE", Section.Events | Section.Rows | Section.Requests,
            new(Parity, "截断提示文字改成发给模型的英文(2.x 是中文)"),
            "超长工具结果的截断提示:2.x 是中文,3.0 是英文;保留的字符数和总字符数两边仍然逐个比较。",
            new TruncationNote()),

        TextRule("TEXT-USER-DENIED", Section.Events | Section.Rows | Section.Requests,
            new(Parity, "用户在审批弹窗上拒绝的结果文字:2.x 是用户给的理由"),
            "用户在审批弹窗上拒绝:2.x 的结果文字是理由本身(没有就是默认话术),3.0 是固定的一句话,有理由时接在后面。",
            new UserDenied()),

        TextRule("TEXT-SUBAGENT-DENIED", Section.Events | Section.Rows | Section.Requests,
            new(Parity, "deny 的话术不再写死 `.next-cowork/settings.local.json`"),
            "子代理被拒的说明里「怎么永久放行」那句:2.x 写死了设置文件路径,3.0 只说「权限的 allow 规则」;前面的话两边逐字相同。",
            new Regex(@"to (permissions\.allow in \.next-cowork/settings\.local\.json|the permission allow rules)\.$", RegexOptions.Compiled), "to <where the allow rules live>."),

        new("TOOL-RESULT-DETAILS", Section.Events | Section.Rows,
            new(Parity, "工具结果带结构化 `details`"),
            "部分工具的结果在 3.0 多带一份给界面的结构化 details(Write 的路径 / diff、TodoWrite 的清单卡片;模型看不到);2.x 的这些结果只有文字。",
            (_, r, _, _) => r.Sum(n => RemoveToolDetails(n))),

        TextRule("TEXT-WRITE-LINE-COUNT", Section.Events | Section.Rows | Section.Requests,
            new(Parity, "`Write` 结果里的行数:文件以换行结尾时 2.x 多数一行"),
            "Write 的结果文字 `Created <path> (N lines, …)`:文件以换行结尾时 2.x 的行数多一(差一的小错),3.0 数真实的行数。",
            new WriteLineCount()),

        new("COMPACTION-PRETOKENS-ESTIMATE", Section.Events | Section.Rows,
            new(Parity, "被动压缩(上游 `context_length` 拒绝)的边界上,`preTokens` 是装配后请求的本地估算"),
            "被动压缩边界上的 preTokens 是装配后请求的本地估算(含系统提示词和工具段,3.0 的提示词更短):只在没有上游真值锚点的被动压缩场景里不比较这一个数,边界的其余字段逐项比较。",
            (l, r, _, scope) =>
            {
                if (scope.Recording.Input["upstream"]!.AsArray()[0]!["kind"]!.GetValue<string>() != "http")
                {
                    return 0;
                }

                return l.Concat(r).Sum(n => MaskPreTokens(n));
            }),

        // ───────────── 流程分叉(登记的偏离,改变了事件 / 消息的形状) ─────────────
        new("ABORT-MERGE-TOOL-RESULTS", Section.Events | Section.Rows,
            new(Loop, "中止收尾把已完成的真实结果和「已中止」占位写在同一条 tool_result 消息里(2.x 分两条)"),
            "中止时已完成的工具结果和「已中止」占位:2.x 先提交一条、再补一条,3.0 合成一条;这里把 2.x 的两条合并后再比较。",
            MergeAbortedResults),

        new("STOP-LENGTH-WITH-TOOL-CALL", Section.Events | Section.Rows | Section.Requests | Section.Runs,
            new(Parity, "`stopReason=length` 带工具调用时不再报错结束,而是所有调用判失败并提示重发(pi 规则,设计 R4 / 02 §2)"),
            "输出上限撞在带工具调用的回合上:2.x 以 outputLimit 错误结束 Run;3.0 不执行调用、给「请重发」结果并继续。分叉点之前的两边逐条比较;分叉之后是 3.0 独有的续跑,不比较。",
            StopLengthWithToolCall),

        // ───────────── 发给上游的请求 ─────────────
        new("LEGACY-HISTORY-THINKING-WITHOUT-SOURCE", Section.Requests,
            new(Parity, "换模型时按「来源未知」丢弃思考块"),
            "2.x 写下的历史里助手消息没有来源信息:3.0 拿不准签名属于谁,回放时一律丢弃思考块;2.x 在同协议下原样回放。",
            (l, _, _, scope) =>
            {
                var texts = scope.Recording.Input["history"]!.AsArray()
                    .SelectMany(m => m!["parts"]!.AsArray())
                    .Where(p => Str(p, "type") == "thinking")
                    .Select(p => Str(p, "text"))
                    .ToHashSet();
                var removed = 0;
                foreach (var request in l)
                {
                    foreach (var message in request["body"]?["messages"]?.AsArray() ?? [])
                    {
                        if (message?["content"] is JsonArray blocks)
                        {
                            for (var i = blocks.Count - 1; i >= 0; i--)
                            {
                                if (Str(blocks[i], "type") == "thinking" && texts.Contains(Str(blocks[i], "thinking")))
                                {
                                    blocks.RemoveAt(i);
                                    removed++;
                                }
                            }
                        }
                    }
                }

                return removed;
            }),

        new("REQ-TOOL-SCHEMA-ZOD-EXTRAS", Section.Requests,
            new(Parity, "工具入参的 JSON schema 不带 `additionalProperties: false`"),
            "工具入参 schema:2.x 由 zod 生成,对象带 additionalProperties:false、整数带安全整数边界;3.0 的 schema 是手写的,不带。其余部分(字段、类型、枚举、长度)逐项比较。",
            (l, _, _, _) => l.Sum(request => RemoveZodExtras(request["body"]?["tools"]))),

        EditBoth("REQ-BUILTIN-TOOL-DESCRIPTIONS", Section.Requests,
            new(Parity, "内置工具(`Read` / `Write`)的描述文字随 C# 实现各自整理"),
            "Read / Write 的描述文字两边各自整理过;只替换这两个内置工具的 description,工具名、schema 仍逐项比较。",
            request =>
            {
                var changed = false;
                foreach (var tool in request["body"]?["tools"]?.AsArray() ?? [])
                {
                    if (Str(tool, "name") is "Read" or "Write" && tool!["description"] is not null)
                    {
                        tool["description"] = "<built-in tool description>";
                        changed = true;
                    }
                }

                return changed;
            }),

        new("REQ-TODO-STATE-BLOCK", Section.Requests,
            new(Parity, "状态块里的待办段 `before_run` 算一次、压缩后附件经 `compact_attachments`"),
            "待办状态块:2.x 每个回合现算(Run 中途写的清单也进状态块,并且挂在最后一条非工具回执的用户消息上);3.0 只在 Run 开始时算一次(保住缓存前缀),所以 Run 里才写出来的清单不会出现。",
            (l, _, _, scope) =>
            {
                if (!scope.Recording.Input["features"]!.AsArray().Any(f => f!.GetValue<string>() == "todos"))
                {
                    return 0;
                }

                var removed = 0;
                foreach (var request in l)
                {
                    foreach (var message in request["body"]?["messages"]?.AsArray() ?? [])
                    {
                        if (message?["content"] is JsonArray blocks)
                        {
                            for (var i = blocks.Count - 1; i >= 0; i--)
                            {
                                if (Str(blocks[i], "text") is { } text && text.StartsWith("<system-reminder>\nWorkspace state, as of the start of this run", StringComparison.Ordinal)
                                    && text.Contains("Your todo list:", StringComparison.Ordinal))
                                {
                                    blocks.RemoveAt(i);
                                    removed++;
                                }
                            }
                        }
                    }
                }

                return removed;
            }),

        EditBoth("REQ-COMPACTION-SUMMARY", Section.Requests,
            new(Parity, "**对话展平成文本**放进一条用户消息"),
            "压缩的摘要请求:提示词模板、对话的组织方式(展平成一条文本消息 + 迭代摘要)、显式关闭思考时的 thinking 字段都不同(登记的 ①②③ 和「压缩摘要请求」新偏离);这里只比较它的模型、输出额度、工具、元数据等外壳。",
            request =>
            {
                var body = request["body"]!.AsObject();
                var system = SystemText(body);
                if (system != "You are a helpful AI assistant tasked with summarizing conversations."
                    && system?.StartsWith("You are a context summarization assistant", StringComparison.Ordinal) != true)
                {
                    return false;
                }

                SetSystemText(body, "<summary system prompt>");
                body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "<summary input>" });
                body.Remove("thinking");
                return true;
            }),

        EditBoth("REQ-SYSTEM-TEXT", Section.Requests,
            new(Parity, "基础提示词按 pi 精简并去掉与工具名绑定的措辞"),
            "系统提示词的正文在 3.0 重写了(只比较它的结构:块数、缓存标记;不比较文字)。",
            request =>
            {
                var body = request["body"]!.AsObject();
                if (SystemText(body) is null || SystemText(body) == "<summary system prompt>")
                {
                    return false;
                }

                SetSystemText(body, "<system prompt>");
                return true;
            }),
    ];

    // ───────────── 构造器 ─────────────

    private static string? E(JsonNode? node) => Str(node, "e");

    private static string? Str(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Rule DropL(string id, Section sections, DocRef doc, string why, Func<JsonNode, bool> match) =>
        new(id, sections, doc, why, (l, _, section, _) => section == Section.Events ? l.RemoveAll(n => match(n)) : 0);

    private static Rule DropR(string id, Section sections, DocRef doc, string why, Func<JsonNode, bool> match) =>
        new(id, sections, doc, why, (_, r, section, _) => section == Section.Events ? r.RemoveAll(n => match(n)) : 0);

    private static Rule EditBoth(string id, Section sections, DocRef doc, string why, Func<JsonNode, bool> edit) =>
        new(id, sections, doc, why, (l, r, _, _) => l.Count(edit) + r.Count(edit));

    // ───────────── 文字改写 ─────────────

    /// <summary>对一棵 JSON 里所有字符串值(键不动)逐个套用改写,返回改了几个。</summary>
    private static int RewriteStrings(JsonNode? node, Func<string, string?> map)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, child) in o.ToList())
                {
                    if (child is JsonValue v && v.TryGetValue<string>(out var text))
                    {
                        if (map(text) is { } changed && changed != text)
                        {
                            o[key] = changed;
                            count++;
                        }
                    }
                    else
                    {
                        count += RewriteStrings(child, map);
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var text))
                    {
                        if (map(text) is { } changed && changed != text)
                        {
                            a[i] = changed;
                            count++;
                        }
                    }
                    else
                    {
                        count += RewriteStrings(a[i], map);
                    }
                }

                break;
        }

        return count;
    }

    private abstract class TextRewrite
    {
        public abstract string? Left(string text, RuleScope scope);

        public abstract string? Right(string text, RuleScope scope);
    }

    private sealed class InvalidArguments : TextRewrite
    {
        private static readonly Regex Js = new(@"^The tool arguments were not valid JSON \(.*?\)\. This is what you sent:\n(?<raw>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Net = new(
            @"^The tool arguments were not valid JSON or were cut off\. Re-issue the tool call with valid arguments\. Details: Invalid tool arguments JSON \(.*?\): (?<raw>.*)$",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public override string? Left(string text, RuleScope scope) => Js.Match(text) is { Success: true } m ? $"[invalid tool arguments]\n{m.Groups["raw"].Value}" : null;

        public override string? Right(string text, RuleScope scope) => Net.Match(text) is { Success: true } m ? $"[invalid tool arguments]\n{m.Groups["raw"].Value}" : null;
    }

    private sealed class TruncationNote : TextRewrite
    {
        private static readonly Regex Zh = new(@"\n\n\[输出已截断:共 (\d+) 字符,只保留前 (\d+) 个\]$", RegexOptions.Compiled);
        private static readonly Regex En = new(@"\n\n\[Output truncated: (\d+) characters in total; only the first (\d+) were kept\]$", RegexOptions.Compiled);

        public override string? Left(string text, RuleScope scope) => Zh.Replace(text, "\n\n[output truncated: $1 total, $2 kept]");

        public override string? Right(string text, RuleScope scope) => En.Replace(text, "\n\n[output truncated: $1 total, $2 kept]");
    }

    private sealed class KeyPrefix : TextRewrite
    {
        public override string? Left(string text, RuleScope scope) => text;

        public override string? Right(string text, RuleScope scope) => text;
    }

    private sealed class WriteLineCount : TextRewrite
    {
        private static readonly Regex Result = new(@"^(Created|Overwrote) (?<path>.+) \((?<n>\d+) lines, ", RegexOptions.Compiled);

        public override string? Left(string text, RuleScope scope)
        {
            if (Result.Match(text) is not { Success: true } m)
            {
                return null;
            }

            // 2.x 数的是 content.split('\n').length:内容以换行结尾时多一。结尾有没有换行,看这次录制里这个文件最终的内容。
            var file = scope.Recording.Expected["files"]!.AsObject().FirstOrDefault(f => f.Key == m.Groups["path"].Value);
            return file.Value?.GetValue<string>().EndsWith('\n') == true
                ? Result.Replace(text, $"$1 $2 ({int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1} lines, ", 1)
                : null;
        }

        public override string? Right(string text, RuleScope scope) => null;
    }

    private sealed class UserDenied : TextRewrite
    {
        private const string Default3 = "The user denied permission for this tool call. Do not retry it unchanged; ask the user how to proceed or take a different approach.";
        private const string WithReason3 = Default3 + " Reason given by the user: ";

        public override string? Left(string text, RuleScope scope)
        {
            if (text == "The user denied this tool call.")
            {
                return "<denied by the user>";
            }

            // 带理由时 2.x 的结果就是理由本身:只认录制里用户真的给过的理由,免得误伤别的文字。
            return scope.Recording.Input["interactions"]!.AsObject().Any(i => i.Value?["reason"]?.GetValue<string>() == text)
                ? $"<denied by the user> reason: {text}"
                : null;
        }

        public override string? Right(string text, RuleScope scope) =>
            text is Default3 or "The user denied this tool call." ? "<denied by the user>"
            : text.StartsWith(WithReason3, StringComparison.Ordinal) ? $"<denied by the user> reason: {text[WithReason3.Length..]}"
            : null;
    }

    private static Rule TextRule(string id, Section sections, DocRef doc, string why, Regex pattern, string replacement) =>
        new(id, sections, doc, why, (l, r, _, _) => l.Sum(n => RewriteStrings(n, t => pattern.IsMatch(t) ? pattern.Replace(t, replacement) : null))
            + r.Sum(n => RewriteStrings(n, t => pattern.IsMatch(t) ? pattern.Replace(t, replacement) : null)));

    private static Rule TextRule(string id, Section sections, DocRef doc, string why, TextRewrite rewrite) =>
        new(id, sections, doc, why, (l, r, _, scope) =>
            l.Sum(n => RewriteStrings(n, t => rewrite.Left(t, scope))) + r.Sum(n => RewriteStrings(n, t => rewrite.Right(t, scope))));

    /// <summary>错误 key:只改名叫 <c>key</c> / <c>messageKey</c> 的属性,2.x 的 <c>agent.error.</c> 前缀和 3.0 的 <c>loop.</c> / <c>llm.</c> 前缀都剥掉。</summary>
    private static Rule TextRule(string id, Section sections, DocRef doc, string why, KeyPrefix _, string left, string[] right) =>
        new(id, sections, doc, why, (l, r, _, _) => l.Sum(n => RewriteKeys(n, left)) + r.Sum(n => right.Sum(p => RewriteKeys(n, p))));

    private static int RewriteKeys(JsonNode? node, string prefix)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, child) in o.ToList())
                {
                    if ((key is "key" or "messageKey") && child is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        o[key] = text[prefix.Length..];
                        count++;
                    }
                    else
                    {
                        count += RewriteKeys(child, prefix);
                    }
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    count += RewriteKeys(child, prefix);
                }

                break;
        }

        return count;
    }

    // ───────────── 请求的系统提示词 ─────────────

    private static string? SystemText(JsonObject body)
    {
        if (body["system"] is JsonArray blocks)
        {
            return string.Concat(blocks.Select(b => Str(b, "text")));
        }

        if (body["instructions"] is JsonValue instructions && instructions.TryGetValue<string>(out var text))
        {
            return text;
        }

        return body["messages"] is JsonArray messages
            ? messages.Where(m => Str(m, "role") is "system" or "developer").Select(m => Str(m, "content")).FirstOrDefault()
            : null;
    }

    private static void SetSystemText(JsonObject body, string text)
    {
        if (body["system"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                block!["text"] = text;
            }
        }
        else if (body["instructions"] is not null)
        {
            body["instructions"] = text;
        }
        else if (body["messages"] is JsonArray messages)
        {
            foreach (var message in messages.Where(m => Str(m, "role") is "system" or "developer"))
            {
                message!["content"] = text;
            }
        }
    }

    private static int MaskPreTokens(JsonNode? node)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject o:
                if (Str(o, "type") == "compact_boundary" && o["preTokens"] is not null)
                {
                    o["preTokens"] = "<local estimate>";
                    count++;
                }

                foreach (var (_, child) in o.ToList())
                {
                    count += MaskPreTokens(child);
                }

                break;
            case JsonArray a:
                count += a.Sum(MaskPreTokens);
                break;
        }

        return count;
    }

    private static int RemoveToolDetails(JsonNode? node)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject o:
                if (o["details"] is not null && o["content"] is not null)
                {
                    o.Remove("details");
                    count++;
                }

                foreach (var (_, child) in o.ToList())
                {
                    count += RemoveToolDetails(child);
                }

                break;
            case JsonArray a:
                count += a.Sum(RemoveToolDetails);
                break;
        }

        return count;
    }

    private static int RemoveZodExtras(JsonNode? node)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject o:
                if (o["additionalProperties"] is JsonValue v && v.TryGetValue<bool>(out var flag) && !flag)
                {
                    o.Remove("additionalProperties");
                    count++;
                }

                foreach (var bound in new[] { "maximum", "minimum" })
                {
                    if (o[bound] is JsonValue b && b.TryGetValue<double>(out var value) && Math.Abs(value) == 9007199254740991d)
                    {
                        o.Remove(bound);
                        count++;
                    }
                }

                foreach (var (_, child) in o.ToList())
                {
                    count += RemoveZodExtras(child);
                }

                break;
            case JsonArray a:
                count += a.Sum(RemoveZodExtras);
                break;
        }

        return count;
    }

    // ───────────── 流程分叉的规则 ─────────────

    private static bool IsToolResultOnly(JsonNode? message) =>
        Str(message, "role") == "user" && message?["parts"] is JsonArray { Count: > 0 } parts && parts.All(p => Str(p, "type") == "tool_result");

    private static bool AllInterrupted(JsonNode? message) =>
        message!["parts"]!.AsArray().All(p => p!["output"]!["content"]!.GetValue<string>().StartsWith(Interrupted, StringComparison.Ordinal));

    private static void PrependParts(JsonNode target, JsonNode source)
    {
        var parts = source["parts"]!.AsArray().Select(p => p!.DeepClone()).ToList();
        for (var k = parts.Count - 1; k >= 0; k--)
        {
            target["parts"]!.AsArray().Insert(0, parts[k]);
        }
    }

    private static int MergeAbortedResults(List<JsonNode> l, List<JsonNode> r, Section section, RuleScope scope)
    {
        var merged = 0;
        if (section == Section.Events)
        {
            for (var i = 0; i < l.Count; i++)
            {
                if (E(l[i]) != "commit" || !IsToolResultOnly(l[i]))
                {
                    continue;
                }

                var j = i + 1;
                while (j < l.Count && E(l[j]) == "tool_end")
                {
                    j++;
                }

                if (j > i + 1 && j + 1 < l.Count && E(l[j]) == "commit" && IsToolResultOnly(l[j]) && AllInterrupted(l[j])
                    && E(l[j + 1]) == "run_end" && Str(l[j + 1], "status") == "aborted")
                {
                    // 把前一条里的真实结果并进后一条(放在前面),删掉前一条;tool_end 的相对顺序不动,正好是 3.0 的「先 tool_end、后一条 commit」。
                    PrependParts(l[j], l[i]);
                    l.RemoveAt(i);
                    merged++;
                    scope.Flags.Add("abort-merge");
                    break;
                }
            }
        }
        else if (section == Section.Rows && scope.Flags.Contains("abort-merge"))
        {
            for (var i = 0; i + 1 < l.Count; i++)
            {
                if (IsToolResultOnly(l[i]) && IsToolResultOnly(l[i + 1]) && AllInterrupted(l[i + 1]))
                {
                    // 和事件部分一样:合并后留下的是后一条的 id(事件里前一条的 commit 已经删掉了,id 的编号要对得上)。
                    PrependParts(l[i + 1], l[i]);
                    l.RemoveAt(i);
                    merged++;
                    break;
                }
            }
        }

        return merged;
    }

    private static bool HasOutputLimitError(JsonNode? message) =>
        Str(message, "role") == "assistant" && message?["parts"] is JsonArray parts
        && parts.Any(p => Str(p, "type") == "tool_call") && parts.Any(p => Str(p, "type") == "error" && Str(p!["error"], "messageKey") == "outputLimit");

    private static void DropOutputLimitError(JsonNode message)
    {
        var parts = message["parts"]!.AsArray();
        parts.Remove(parts.First(p => Str(p, "type") == "error"));
    }

    private static int RewriteNotExecuted(JsonNode node) => RewriteStrings(node, t => t == UpstreamIncomplete ? OutputLimitResult : null);

    private static int StopLengthWithToolCall(List<JsonNode> l, List<JsonNode> r, Section section, RuleScope scope)
    {
        switch (section)
        {
            case Section.Events:
            {
                var k = l.FindIndex(n => E(n) == "commit" && HasOutputLimitError(n));
                if (k < 0)
                {
                    return 0;
                }

                DropOutputLimitError(l[k]);
                var count = 1 + l.Skip(k).Sum(RewriteNotExecuted);
                l.RemoveAll(n => E(n) == "run_end" && Str(n["error"], "key") == "outputLimit");
                var results = l.FindIndex(k, n => E(n) == "commit" && IsToolResultOnly(n));
                var rr = r.FindIndex(n => E(n) == "commit" && IsToolResultOnly(n) && n["parts"]![0]!["output"]!["content"]!.GetValue<string>() == OutputLimitResult);
                if (results >= 0 && rr >= 0)
                {
                    // 分叉之后的 3.0 续跑(下一轮请求、再一条回复、run_end)不比较。
                    r.RemoveRange(rr + 1, r.Count - rr - 1);
                }

                scope.Flags.Add("stop-length");
                return count;
            }

            case Section.Rows when scope.Flags.Contains("stop-length"):
            {
                var k = l.FindIndex(HasOutputLimitError);
                DropOutputLimitError(l[k]);
                var count = 1 + l.Skip(k).Sum(RewriteNotExecuted);
                var rr = r.FindIndex(n => IsToolResultOnly(n) && n["parts"]![0]!["output"]!["content"]!.GetValue<string>() == OutputLimitResult);
                if (rr >= 0)
                {
                    r.RemoveRange(rr + 1, r.Count - rr - 1);
                }

                return count;
            }

            case Section.Requests when scope.Flags.Contains("stop-length") && r.Count > l.Count:
                r.RemoveRange(l.Count, r.Count - l.Count);
                return 1;
            case Section.Runs when scope.Flags.Contains("stop-length"):
                l[0] = new JsonObject { ["status"] = "done" };
                return 1;
            default:
                return 0;
        }
    }
}
