using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;
using Ncw.Agent.Tools.Schema;
using Ncw.Agent.Widgets.Actions;
using Ncw.Agent.Widgets.Cards;
using Ncw.Agent.Widgets.Guidelines;
using Ncw.Agent.Widgets.Sanitizing;

namespace Ncw.Agent.Widgets;

/// <summary>可视化工具的装配入口:Host 在造注册表时调 <see cref="Create"/>。</summary>
public static class VisualizeTools
{
    public static IReadOnlyList<IAgentTool> Create(PermissionRuns runs, IWidgetTranscript transcript) =>
    [
        new VisualizeReadMeTool(),
        new VisualizeShowWidgetTool(runs),
        new VisualizeEditWidgetTool(transcript),
        new VisualizeUpdateWidgetTool(transcript),
    ];

    /// <summary>这些工具的内部 id(Host 的已知工具表 / 动作的禁用表用)。</summary>
    public static IReadOnlyList<string> ToolIds { get; } = ["visualize_read_me", "visualize_show_widget", "visualize_edit_widget", "visualize_update_widget"];
}

/// <summary>
/// 载荷从模型手里到卡片的流水线:格式判定 → 大小上限 → 消毒(桌面用)→ 静态快照(手机端降级用)。三个会产出卡片的工具共用,规则只有一份。
/// </summary>
internal static partial class WidgetPipeline
{
    /// <summary>
    /// <c>widget_code</c> 的长度上限(字符)。128K 约等于一份很大的 SVG 图或一个带内联数据的仪表盘。上限存在的理由是<b>落盘</b>:这张卡跟着工具结果进转录,
    /// 没有上限的话一次失控的生成会往 SQLite 里写进去多少就得跟着读回来多少。超了就给模型一句明确的失败(它自己会知道要拆小),而不是静默截断一张画不出来的图。
    /// </summary>
    public const int MaxCodeChars = 128 * 1024;

    public const int MaxDataChars = 64 * 1024;

    public const string RenderedNote =
        "Content rendered and shown to the user. Do not duplicate the shown content in text — it is already visually represented. Keep any remaining explanation in your normal reply text.";

    [GeneratedRegex(@"^\s*(?:import\s|export\s+default\b)|^\s*import\s[^;]*from\s+['""](?:react|nextcowork/)", RegexOptions.Multiline)]
    private static partial Regex TsxPattern();

    [GeneratedRegex(@"^[a-z0-9_]+$")]
    private static partial Regex TitlePattern();

    public static string? CheckTitle(string title) =>
        title.Length is >= 1 and <= 64 && TitlePattern().IsMatch(title) ? null : "title must be a short snake_case identifier, e.g. q4_revenue_by_product_line (lowercase letters, digits and underscores, at most 64 characters).";

    /// <summary>没给 <c>format</c> 时的推断:<c>&lt;svg</c> 开头 = svg,否则 html;TSX 的特征被明确拒绝(3.0 不做 TSX)。</summary>
    public static string ResolveFormat(string? declared, string code)
    {
        if (declared == "tsx" || TsxPattern().IsMatch(code))
        {
            throw new ToolFailureException(
                "TSX widgets are not supported on this host. Write the widget as \"html\" (an HTML fragment) or \"svg\" (raw <svg>); use inline <script> for interactivity.");
        }

        return declared is "svg" or "html" ? declared : code.AsSpan().TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ? "svg" : "html";
    }

    public sealed record Prepared(string Format, SanitizeResult Code, string Snapshot);

    public static Prepared Prepare(string code, string format)
    {
        if (code.Length > MaxCodeChars)
        {
            throw new ToolFailureException($"widget_code is {code.Length.ToString(CultureInfo.InvariantCulture)} characters; the limit is {MaxCodeChars.ToString(CultureInfo.InvariantCulture)}. Split it into smaller visuals or reduce the inline data.");
        }

        var sanitized = WidgetSanitizer.Sanitize(code, SanitizeMode.Interactive);
        if (string.IsNullOrWhiteSpace(sanitized.Code))
        {
            throw new ToolFailureException("Nothing renderable was left in widget_code after unsafe content was removed." + DescribeFindings(sanitized.Findings));
        }

        var snapshot = WidgetSanitizer.Sanitize(sanitized.Code, SanitizeMode.Static).Code;
        return new Prepared(format, sanitized, snapshot);
    }

    /// <summary>给模型看的「拿掉了什么」。每条规则带一句怎么改,下一次调用就能写对。</summary>
    public static string DescribeFindings(IReadOnlyList<SanitizeFinding> findings)
    {
        if (findings.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder("\n\nUnsafe or unsupported content was removed from the widget before it was shown:");
        foreach (var f in findings.Take(12))
        {
            sb.Append("\n- ").Append(f.Rule).Append(": ").Append(f.Detail);
            if (f.Count > 1)
            {
                sb.Append(" (x").Append(f.Count.ToString(CultureInfo.InvariantCulture)).Append(')');
            }

            sb.Append(" — ").Append(Hint(f.Rule));
        }

        return sb.ToString();
    }

    private static string Hint(string rule) => rule switch
    {
        "attr.event" => "inline event-handler attributes (onclick, onload, ...) do not run; attach listeners with addEventListener in a <script> placed last.",
        "attr.forbidden" => "this attribute is not allowed.",
        "url.scheme" => "javascript:, non-image data: and similar URLs are not allowed.",
        "url.external" => "external images and references are not allowed; embed images as data:image/... URLs, and reference only #ids inside the widget.",
        "script.external" => "scripts may only load from cdnjs.cloudflare.com, cdn.jsdelivr.net, unpkg.com or esm.sh over https.",
        "script.type" => "only plain JavaScript (and type=\"module\") scripts are allowed.",
        "script.svg" => "put <script> in the HTML part after the <svg>, not inside it.",
        "link.external" => "only <link rel=\"stylesheet\"> from the allowed CDNs is kept.",
        "css.unsafe" => "CSS with @import, external url(), expression() and similar is not allowed; url(#id) and data:image URLs are fine.",
        "animate.href" => "animating href or event attributes is not allowed.",
        "element.forbidden" => "this element cannot be used in a widget.",
        "element.unsupported" => "this element is not supported in widgets; the tag was dropped and its text kept.",
        "element.depth" => "the markup is nested too deeply.",
        _ => "removed.",
    };

    public static JsonElement CardDetails(ExecutionToolContext ctx, string title, Prepared prepared, bool interactive, IReadOnlyList<WidgetActionDef> actions, string? submitted = null) =>
        WidgetCards.Details(ctx.Runtime, title, prepared.Format, prepared.Code, prepared.Snapshot, interactive, actions, submitted);
}

/// <summary>
/// <c>visualize_read_me</c>:返回设计规范原文(按模块切)。返回值<b>原样</b>是规范正文,不加我们自己的前后缀——正文里满是「见上 / 见下」的交叉引用。
/// ★ 长度检查不是多余的:结果在循环层按 64K 字符截断,<c>diagram + chart</c> 装配出来 70K+,截断的表现是规范从中间断掉而模型不觉得少了什么(2.x 的同一条理由),所以明确失败并给出每个模块的体积。
/// </summary>
public sealed class VisualizeReadMeTool() : BuiltinTool(
    "visualize_read_me",
    "Returns the required context for visualize_show_widget: CSS variables, color ramps, typography, layout rules, streaming-safe patterns and worked examples. "
    + "Call it once before your first visualize_show_widget call, and again later if you need a different module. "
    + "Do NOT mention or narrate this call to the user — it is an internal setup step. Call it silently and go straight to building the visualization. "
    + "NOTE: this host does not provide sendPrompt() — ignore that function where the guidelines mention it; put follow-up options in your reply text instead. "
    + "Inline event-handler attributes (onclick=...) do not run in widgets: attach listeners with addEventListener in a <script> placed last. "
    + "Widgets are HTML or SVG only (there is no TSX format).",
    S.Build(S.Object(
        ["modules"],
        ("modules", S.Array(
            "Which module(s) to load. Pick every one that fits the visual you are about to build. At most two — the largest combination (diagram + chart) does not fit in one response.",
            S.Enum("A guideline module", "diagram", "mockup", "interactive", "chart", "art"),
            minItems: 1,
            maxItems: 2)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Load the design guidelines for inline visuals",
    ["Call visualize_read_me once, silently, before the first visualize_show_widget call."])
{
    /// <summary>循环层的工具输出截断线(<c>LoopHooks.MaxToolOutputChars</c> 的默认值)。</summary>
    public const int OutputLimit = 64 * 1024;

    protected override ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var modules = args.Arr("modules").Where(static m => m.ValueKind == JsonValueKind.String).Select(static m => m.GetString()!).ToList();
        var text = WidgetGuidelines.Get(modules);
        if (text.Length > OutputLimit)
        {
            var sizes = string.Join(", ", modules.Select(m => $"{m}: {WidgetGuidelines.Get([m]).Length.ToString(CultureInfo.InvariantCulture)} chars"));
            throw Fail(
                $"These modules together are {text.Length.ToString(CultureInfo.InvariantCulture)} characters and the result is capped at {OutputLimit.ToString(CultureInfo.InvariantCulture)}, "
                + $"so returning them would cut the guidelines off mid-rule. Call again with a single module. Sizes requested: {sizes}.");
        }

        return ValueTask.FromResult(ToolResult.Text(text, WidgetCards.Simple(ctx.Runtime, "visualizeReadMe", w =>
        {
            w.WriteStartArray("modules");
            foreach (var m in modules)
            {
                w.WriteStringValue(m);
            }

            w.WriteEndArray();
        })));
    }
}

/// <summary>
/// <c>visualize_show_widget</c>:收 HTML / SVG 载荷,产出内联卡片。
/// <list type="bullet">
/// <item>产出物经服务端消毒(<see cref="WidgetSanitizer"/>),卡片 Details 带消毒后的 <c>code</c>(桌面渲染)和 <c>snapshot</c>(静态快照,手机端降级)。<c>Content</c>(给模型的)不回灌代码:它刚写完。</item>
/// <item><c>interactive: true</c>:先把卡片作为实时卡片推出去(<c>ToolProgress.Details</c>),再经交互闸门挂起一次 <c>widget_submit</c>,等 widget 的 <c>submit()</c>(<c>widgets.submit</c>,先到先得);子代理里不提供。</item>
/// <item><c>actions</c>:预先声明的动作,写进卡片;<c>widgets.runAction</c> 由 Agent 经权限闸代参执行(工具与入参在这里定死,widget 只能选 id 和受约束的参数)。子代理里不提供。</item>
/// <item><c>ReadOnly: true</c>:不碰工作区、不改任何状态;<c>NeedsNetwork: true</c>:它自己一个字节都不出网,但它产出的东西会(CDN 脚本),事实描述(2.x 同)。<c>ConcurrencySafe: false</c>:edit / update 按转录里的顺序找卡。</item>
/// </list>
/// </summary>
public sealed class VisualizeShowWidgetTool(PermissionRuns runs) : BuiltinTool(
    "visualize_show_widget",
    "Show visual content — SVG graphics, diagrams, charts, or interactive widgets — that renders inline in the conversation, with no frame around it. "
    + "Two formats: \"svg\" (raw <svg>) and \"html\" (an HTML fragment; inline <script> runs, so sliders, charts and calculators work). "
    + "IMPORTANT: call visualize_read_me before your first call, and never narrate that call — call it silently and then respond as if you went straight to building the visualization. "
    + "Inline event-handler attributes (onclick=...), iframes, external images and scripts outside cdnjs.cloudflare.com / cdn.jsdelivr.net / unpkg.com / esm.sh are removed. "
    + "Set interactive: true when you need the user to pick or configure something in the widget: the call then waits until the widget calls submit(value), and you receive that value as the result. "
    + "Declare actions when the widget needs buttons that run a fixed tool call (e.g. re-run tests, re-read a file); the user approves them through the normal permission flow.",
    S.Build(S.Object(
        ["title", "widget_code", "loading_messages"],
        ("title", S.String(
            "Short snake_case identifier for this visual. Must disambiguate — if the conversation has several visuals, this title alone should say which one is meant (q4_revenue_by_product_line, not chart). visualize_edit_widget and visualize_update_widget refer to the widget by this title.",
            minLength: 1, maxLength: 64)),
        ("format", S.Enum("Format of widget_code. If omitted it is inferred: <svg → svg, otherwise html.", "html", "svg")),
        ("widget_code", S.String(
            "The code to render. svg: raw SVG starting with <svg>. html: a fragment — no <!DOCTYPE>, <html>, <head> or <body>; use the CSS variables from visualize_read_me, keep the background transparent, put <script> last. "
            + "Never wrap the outermost element in a card, border or background — the widget sits directly in the conversation.",
            minLength: 1, maxLength: WidgetPipeline.MaxCodeChars)),
        ("loading_messages", S.Array(
            "1–4 short messages shown while the visual streams in, each roughly 5 words, in the language the user is writing. For serious topics (illness, death, war, disaster, trauma) keep them deliberately flat and procedural — no evocative wording.",
            S.String("A loading message", minLength: 1, maxLength: 120), minItems: 1, maxItems: 4)),
        ("interactive", S.Boolean("Wait for the user to submit a value from the widget (window.ncw.submit(value)) and return it.")),
        ("actions", S.Array(
            "Fixed tool calls the widget may trigger by id (window.ncw.runAction(id, params)). Results go back to the widget, not to you.",
            S.Object(
                ["id", "label", "tool", "input"],
                ("id", S.String("Short snake_case id.", minLength: 1, maxLength: 40)),
                ("label", S.String("Shown to the user in the confirmation dialog.", minLength: 1, maxLength: 60)),
                ("tool", S.String("Name of an existing tool, e.g. Bash or Read.", minLength: 1, maxLength: 128)),
                ("input", S.Object([])),
                ("params", S.Object([]))),
            maxItems: WidgetActions.MaxActions)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: true, ConcurrencySafe: false),
    "Show an inline HTML/SVG visual (chart, diagram, widget)",
    [
        "Call visualize_read_me once before the first visual, silently.",
        "Put explanations in your reply text, not inside the widget; do not repeat in text what the visual already shows.",
    ])
{
    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var title = RequiredString(args, "title").Trim();
        if (WidgetPipeline.CheckTitle(title) is { } titleProblem)
        {
            throw Fail(titleProblem);
        }

        var interactive = args.Bool("interactive") == true;
        IReadOnlyList<WidgetActionDef> actions;
        try
        {
            actions = args.TryGetProperty("actions", out var raw) && raw.ValueKind == JsonValueKind.Array ? WidgetActions.Parse(raw) : [];
        }
        catch (WidgetActionException e)
        {
            throw Fail(e.Message);
        }

        // 交互模式与动作只开给主 Run:子代理没有人能回答它的挂起(一个等提交的 widget 在子代理里就是一次死锁),也没有人能确认它的动作。
        if ((interactive || actions.Count > 0) && ctx.Depth != 0)
        {
            throw Fail("interactive widgets and actions are unavailable here (subagents cannot wait for the user). Call again without interactive and actions.");
        }

        var code = RequiredString(args, "widget_code");
        var prepared = WidgetPipeline.Prepare(code, WidgetPipeline.ResolveFormat(args.Str("format"), code));
        var note = WidgetPipeline.DescribeFindings(prepared.Code.Findings);
        var details = WidgetPipeline.CardDetails(ctx, title, prepared, interactive, actions);
        if (!interactive)
        {
            return ToolResult.Text(WidgetPipeline.RenderedNote + note, details);
        }

        var interactions = runs.Get(ctx.RunId)?.Interactions;
        if (interactions is not { CanInteract: true })
        {
            throw Fail("interactive widgets are unavailable here (headless runs cannot wait for the user). Call again without interactive.");
        }

        // 实时卡片:工具还没返回,转录里还没有结果——先把卡片推出去,用户才有东西可以点。
        ctx.Progress.Report(new ToolProgress(null, null, details));
        InteractionResponse response;
        try
        {
            response = await interactions.RequestAsync(new WidgetSubmit(ctx.CallId, title), ct).ConfigureAwait(false);
        }
        catch (InteractionRefusedException)
        {
            throw Fail("interactive widgets are unavailable here. Call again without interactive.");
        }

        if (response is not WidgetSubmitResponse submit)
        {
            throw Fail("Unexpected interaction response.");
        }

        if (submit.Value is null)
        {
            throw Fail("The user closed the widget without submitting. Do not assume an answer.");
        }

        var final = WidgetPipeline.CardDetails(ctx, title, prepared, interactive, actions, submit.Value);
        return ToolResult.Text(
            $"The user submitted the widget. Submitted value (JSON): {submit.Value}\nThe widget is still shown to the user — do not redraw it; continue based on the submitted value." + note,
            final);
    }
}

/// <summary>
/// <c>visualize_edit_widget</c>:在上一版的基础上改几处,而不是整份重写(照 <c>Edit</c> 的规矩:旧串必须恰好出现一次,或显式 <c>replace_all</c>,否则失败并说清楚——不猜)。
/// 编辑作用在转录里那张卡的<b>消毒后源码</b>上(没有被消毒改动的载荷与模型写的逐字相同);产出一张<b>新卡</b>,上一版留在原处(改写历史会让「上一轮看到的图」在用户眼前悄悄变掉)。
/// 交互模式不继承(上一版已经交过卷);动作照旧继承(它们是这张图的按钮)。
/// </summary>
public sealed class VisualizeEditWidgetTool(IWidgetTranscript transcript) : BuiltinTool(
    "visualize_edit_widget",
    "Make targeted edits to a widget shown earlier in this conversation (found by its title) and show the updated version. "
    + "Each edit replaces old_string with new_string in the widget source; old_string must match exactly once unless replace_all is set. "
    + "Prefer this over rewriting the whole widget for small changes.",
    S.Build(S.Object(
        ["title", "edits"],
        ("title", S.String("Title of the existing widget to edit.", minLength: 1, maxLength: 64)),
        ("new_title", S.String("Title for the new version. Defaults to the same title.", minLength: 1, maxLength: 64)),
        ("edits", S.Array(
            "The replacements, applied in order.",
            S.Object(
                ["old_string", "new_string"],
                ("old_string", S.String("Exact text to replace", minLength: 1)),
                ("new_string", S.String("Replacement text")),
                ("replace_all", S.Boolean("Replace every occurrence"))),
            minItems: 1,
            maxItems: 32)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: true, ConcurrencySafe: false),
    "Edit an inline visual shown earlier",
    ["Use visualize_edit_widget for small changes to an existing visual instead of redrawing it."])
{
    protected override ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var title = RequiredString(args, "title");
        var newTitle = args.Str("new_title") ?? title;
        if (WidgetPipeline.CheckTitle(newTitle) is { } titleProblem)
        {
            throw Fail(titleProblem);
        }

        var baseCard = WidgetCards.FindByTitle(transcript.History(ctx.SessionId), title)
            ?? throw Fail($"No widget titled \"{title}\" in this conversation. Use visualize_show_widget to create it.");
        var code = baseCard.Code;
        var index = 0;
        foreach (var edit in args.Arr("edits"))
        {
            index++;
            var oldString = edit.Str("old_string") ?? "";
            var newString = edit.Str("new_string") ?? "";
            var all = edit.Bool("replace_all") == true;
            var count = CountOccurrences(code, oldString);
            if (count == 0)
            {
                throw Fail($"Edit {index.ToString(CultureInfo.InvariantCulture)}: old_string was not found in the widget source.");
            }

            if (count > 1 && !all)
            {
                throw Fail($"Edit {index.ToString(CultureInfo.InvariantCulture)}: old_string matches {count.ToString(CultureInfo.InvariantCulture)} places. Add more context to make it unique, or set replace_all.");
            }

            code = all ? code.Replace(oldString, newString, StringComparison.Ordinal) : ReplaceFirst(code, oldString, newString);
        }

        var prepared = WidgetPipeline.Prepare(code, WidgetPipeline.ResolveFormat(baseCard.Format, code));
        var details = WidgetPipeline.CardDetails(ctx, newTitle, prepared, interactive: false, baseCard.Actions);
        return ValueTask.FromResult(ToolResult.Text(WidgetPipeline.RenderedNote + WidgetPipeline.DescribeFindings(prepared.Code.Findings), details));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string needle, string replacement)
    {
        var at = text.IndexOf(needle, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + needle.Length));
    }
}

/// <summary>
/// <c>visualize_update_widget</c>:给一张已经画出来的 widget 推一份新数据,不重画(长任务的进度、分析做到一半的中间结果——图是同一张,变的只有数)。
/// 数据<b>只走卡片</b>(<c>kind: "widgetData"</c>),不回灌给模型;宿主按 <c>title</c> 把最新一份送到那张卡上(widget 里 <c>window.ncw.onData(callback)</c> 读它)。
/// </summary>
public sealed class VisualizeUpdateWidgetTool(IWidgetTranscript transcript) : BuiltinTool(
    "visualize_update_widget",
    "Push new data into an HTML widget already shown in this conversation (found by its title) without redrawing it. "
    + "The widget reads the latest value with window.ncw.onData(callback). Use for progress, live results, or updated numbers.",
    S.Build(S.Object(
        ["title"],
        ("title", S.String("Title of the existing widget.", minLength: 1, maxLength: 64)),
        ("data", new System.Text.Json.Nodes.JsonObject { ["description"] = "Any JSON value. Replaces the previous data." }))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false, ConcurrencySafe: false),
    "Push new data into an inline visual",
    [])
{
    protected override ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var title = RequiredString(args, "title");
        if (WidgetCards.FindByTitle(transcript.History(ctx.SessionId), title) is null)
        {
            throw Fail($"No widget titled \"{title}\" in this conversation.");
        }

        var data = args.TryGetProperty("data", out var value) ? value : Jsn.Parse("null");
        var length = data.GetRawText().Length;
        if (length > WidgetPipeline.MaxDataChars)
        {
            throw Fail($"data is {length.ToString(CultureInfo.InvariantCulture)} characters; the limit is {WidgetPipeline.MaxDataChars.ToString(CultureInfo.InvariantCulture)}. Send a smaller summary.");
        }

        return ValueTask.FromResult(ToolResult.Text(
            $"Data pushed to widget {title}. Do not describe the new values again in text.",
            WidgetCards.DataDetails(ctx.Runtime, title, data)));
    }
}
