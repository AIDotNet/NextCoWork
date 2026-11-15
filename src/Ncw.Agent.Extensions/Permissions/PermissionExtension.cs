using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Extensions.Permissions;

public sealed class PermissionOptions
{
    /// <summary>
    /// 把 <see cref="BashCommand.Classify"/> 认定为只读的 Bash 命令当作只读工具放行(Claude Code 的做法)。<b>缺省关</b>:2.x 里 Bash 恒为「非只读、破坏性」,
    /// 白名单再窄也是对 2.x 语义的放宽,所以开不开由宿主决定(设置项),而不是权限扩展自作主张。
    /// </summary>
    public bool AutoAllowReadOnlyBash { get; init; }
}

/// <summary>
/// 权限扩展(03 §3,一方扩展,<b>不可关闭</b>):挂在 <c>before_tool</c> 的第一位(<see cref="Order"/> 最小),并贡献系统提示词里的「权限事实」一节。
/// 它的异常按<b>拒绝</b>处理(失败即关闭,06 §2);Run 没有登记权限状态也是拒绝。
/// <para>
/// 决策顺序(顺序即语义,2.x ★;<see cref="PermissionDecisions"/> 是它的纯函数形式):
/// ① 联网开关关闭且工具受它管 → 拒绝(<c>full</c> 也一样);② deny 规则 → 拒绝,<b>不运行任何钩子</b>;③ PreToolUse 钩子 + 插件拦截器(拦截器只能收紧);
/// ④ 钩子拒绝 → 拒绝(压过 allow 规则);⑤ 钩子要求询问或命中 ask 规则 → 直接去问人;⑥ 钩子放行;⑦ 只读工具 / 档位放行;⑧ allow 规则;
/// ⑨ <c>auto</c> 档 + 破坏性 → AI 审核(不明确就交给人);⑩ 子代理 → 拒绝(它没有人可问);⑪ 发起 <c>tool_permission</c> 交互,等待回答。
/// </para>
/// </summary>
public sealed class PermissionExtension : IAgentExtension
{
    public const string ExtensionId = "permissions";

    private readonly PermissionRuns _runs;
    private readonly PermissionRuleBook _rules;
    private readonly IPermissionReviewer? _reviewer;
    private readonly IReadOnlyList<IPreToolVerdictSource> _hooks;
    private readonly IReadOnlyList<IToolInterceptor> _interceptors;
    private readonly PermissionOptions _options;

    public PermissionExtension(
        PermissionRuns runs,
        PermissionRuleBook rules,
        IPermissionReviewer? reviewer = null,
        IEnumerable<IPreToolVerdictSource>? hooks = null,
        IEnumerable<IToolInterceptor>? interceptors = null,
        PermissionOptions? options = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _reviewer = reviewer;
        _hooks = hooks?.ToList() ?? [];
        _interceptors = interceptors?.ToList() ?? [];
        _options = options ?? new PermissionOptions();
    }

    public string Id => ExtensionId;

    /// <summary>权限扩展固定在最前。</summary>
    public int Order => int.MinValue;

    public void Register(IExtensionRegistry registry)
    {
        registry.Gate(
            LoopExtensionPoints.BeforeTool,
            DecideAsync,
            static e => PreflightDecision.Deny($"The tool call was not permitted because the permission check failed: {e.Message}"));

        // run_end 的兜底:Run 正常收尾 / 中止时清理它挂起的交互,并摘掉登记(幂等;取消令牌本来已经清理过了)。
        registry.On(LoopExtensionPoints.RunEnd, (ev, ctx, _) =>
        {
            _runs.Get(ctx.RunId)?.Interactions.End(ev.Status);
            _runs.Remove(ctx.RunId);
            return ValueTask.CompletedTask;
        });

        registry.ContributeSystemSection("permissions", info =>
            _runs.ForSession(info.SessionId) is { } run ? PermissionGate.Facts(run.Mode, run.WebSearch) : null);
    }

    // ───────────────────────── 规则比对的对象 ─────────────────────────

    private sealed record Target(RuleSubjects Subjects, bool Outside, EnvPath? FilePath, CommandRisk? Risk);

    /// <summary>
    /// 算出一次调用的比对对象。路径字段规范化(<c>..</c>、符号链接、相对 / 绝对写法)之后再比:2.x 拿原始字符串比,
    /// <c>Write(src/*)</c> 这条 allow 能被 <c>src/../../etc/x</c> 匹配上(通配 <c>*</c> 匹配 <c>/</c>)。allow 只认规范写法(<see cref="RuleSubjects.Strict"/>),
    /// deny / ask 额外认原始写法和 Bash 的各个子命令(<see cref="RuleSubjects.Loose"/>)。
    /// </summary>
    private static async ValueTask<Target> BuildTargetAsync(PermissionRunState run, JsonElement input, CancellationToken ct)
    {
        var strict = new List<string>();
        var loose = new List<string>();
        var outside = false;
        EnvPath? filePath = null;
        CommandRisk? risk = null;
        if (input.ValueKind != JsonValueKind.Object)
        {
            return new Target(RuleSubjects.Empty, false, null, null);
        }

        string? Field(string name) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } t ? t : null;

        if (Field("command") is { } command)
        {
            strict.Add(command);
            loose.AddRange(BashCommand.Segments(command));
            risk = BashCommand.Classify(command);
        }

        foreach (var name in new[] { "file_path", "path" })
        {
            if (Field(name) is not { } raw)
            {
                continue;
            }

            loose.Add(raw);
            try
            {
                var resolved = await run.Environment.Paths.ResolveAsync(run.Root, raw, ct).ConfigureAwait(false);
                var forms = new List<string>();
                if (!resolved.Outside && await run.RealRootAsync(ct).ConfigureAwait(false) is { } realRoot && resolved.Absolute.IsWithin(realRoot))
                {
                    var relative = resolved.Absolute.RelativeTo(realRoot);
                    forms.Add(relative.Length == 0 ? "." : relative);
                }

                forms.Add(resolved.Absolute.Value);
                strict.AddRange(forms);
                loose.AddRange(forms);
                outside |= resolved.Outside;
                if (name == "file_path")
                {
                    filePath = resolved.Absolute;
                }
            }
            catch (EnvironmentException)
            {
                // 解析不了(非法路径 / 连接断开):没有规范写法,allow 规则对它不起作用,deny / ask 仍按原始写法比。
            }
        }

        foreach (var name in new[] { "pattern", "query" })
        {
            if (Field(name) is { } text)
            {
                strict.Add(text);
                loose.Add(text);
            }
        }

        if (input.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && url.GetString()?.Trim() is { Length: > 0 } rawUrl)
        {
            strict.Add(rawUrl);
            loose.Add(rawUrl);
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            {
                var domain = $"domain:{uri.Host.ToLowerInvariant()}";
                strict.Add(domain);
                loose.Add(domain);
            }
        }

        // Loose ⊇ Strict:放行只认确切规范的东西,拒绝和强制询问宁可多命中。
        foreach (var s in strict.Where(s => !loose.Contains(s)).ToList())
        {
            loose.Add(s);
        }

        return new Target(new RuleSubjects(strict, loose.Distinct(StringComparer.Ordinal).ToList()), outside, filePath, risk);
    }

    // ───────────────────────── 判定 ─────────────────────────

    private static bool IsPlanTrusted(PermissionRunState run, string internalId, Target target)
    {
        if (internalId is "EnterPlanMode" or "ExitPlanMode")
        {
            return run.PlanWorkflow;
        }

        // 计划模式把 Write / Edit 围在一个计划文件里,所以它们的审批不弹两次。3.0 收紧:权限层自己再核对一遍,只有改的正是围栏文件才算只读
        // (2.x 只按工具名信任,围栏是否真的设上了全靠写工具自己)。
        return internalId is "Write" or "Edit"
            && run.PlanFence is { } fence
            && target.FilePath is { } path
            && string.Equals(fence.Value, path.Value, PathRules.Comparison(fence.Style));
    }

    private async ValueTask<PreflightDecision> DecideAsync(ToolInvocation call, ExtensionContext ctx, CancellationToken ct)
    {
        var run = _runs.Get(ctx.RunId);
        if (run is null)
        {
            // 失败即关闭:不知道这个 Run 的档位、环境和项目根,就没有任何依据放行。
            return PreflightDecision.Deny("The tool call was not permitted because no permission state is registered for this run.");
        }

        var info = call.Tool.Info;
        var traits = info.Traits;
        var target = await BuildTargetAsync(run, call.Input, ct).ConfigureAwait(false);
        var risk = target.Risk;
        var mode = run.Mode;
        var planTrusted = IsPlanTrusted(run, info.InternalId, target);
        var readOnly = traits.ReadOnly
            || planTrusted
            || (_options.AutoAllowReadOnlyBash && info.InternalId == "Bash" && risk == CommandRisk.ReadOnly);
        var outcome = PermissionGate.Evaluate(new PermissionQuery(
            mode, readOnly, traits.Destructive, run.WebSearch, PermissionGate.NetworkSwitchTools.Contains(info.InternalId)));

        var rules = await _rules.ReadAsync(run.RuleContext, ct).ConfigureAwait(false);
        var early = PermissionDecisions.DecideBeforeHooks(outcome, PermissionRules.MatchFirst(rules.Deny, info.InternalId, target.Subjects.Loose));
        if (early.Denied)
        {
            return PreflightDecision.Deny(early.Reason);
        }

        var verdict = await CollectVerdictAsync(new PreToolContext(run, call, call.Input), ctx.Log, ct).ConfigureAwait(false);
        var final = PermissionDecisions.DecideAfterHooks(
            outcome,
            verdict,
            PermissionRules.MatchFirst(rules.Ask, info.InternalId, target.Subjects.Loose),
            PermissionRules.MatchFirst(rules.Allow, info.InternalId, target.Subjects.Strict),
            // 危险命令不交给模型审核:审核器是个 128 token 的短请求,它说 allow 不该能替人放行 `rm -rf`。
            autoReview: mode == PermissionMode.Auto && traits.Destructive && risk != CommandRisk.Dangerous);
        switch (final.Kind)
        {
            case FinalKind.Deny:
                return PreflightDecision.Deny(final.Reason);
            case FinalKind.Allow:
                return PreflightDecision.Allow;
            case FinalKind.Review:
                switch (await ReviewAsync(run, call, ct).ConfigureAwait(false))
                {
                    case ReviewResult.Allow:
                        return PreflightDecision.Allow;
                    case ReviewResult.Deny:
                        return PreflightDecision.Deny("The configured AI reviewer denied this potentially unsafe operation.");
                }

                break; // Unknown → 问人
        }

        // 子代理到此为止——它没有人可问。当场拒绝而不是挂起(2.x 真实发生过的死锁:跑满一小时、六百多次工具调用之后毫无进展)。
        // 拒绝进转录、模型看得见,它可以换个做法或者把这一步交回给主代理。位置在钩子与规则之后:一条 allow 规则仍然应该让子代理跑起来。
        var suggested = PermissionRules.Suggest(info.InternalId, target.Subjects.Strict, exactBash: risk == CommandRisk.Dangerous);
        if (run.IsSubagent)
        {
            return PreflightDecision.Deny(
                "This tool needs the user's approval, and a subagent cannot ask for it. "
                + $"Either do this step without {call.Name}, or report back that the parent agent must run it. "
                + $"To allow it unattended in future runs, the user can add \"{suggested}\" to the permission allow rules.");
        }

        var request = new ToolPermission
        {
            CallId = call.CallId,
            ToolName = call.Name,
            InternalId = info.InternalId,
            Input = call.Input.Clone(),
            Traits = traits,
            Env = run.EnvironmentRef,
            SuggestedRule = suggested,
            AllowedScopes = RememberScopes.Session | RememberScopes.Global | (run.Root is null ? RememberScopes.None : RememberScopes.Project),
            Risk = risk,
            OutsideWorkspace = target.Outside,
        };

        var response = await run.Interactions.RequestAsync(request, ct).ConfigureAwait(false);
        if (response is not ToolPermissionResponse permission)
        {
            return PreflightDecision.Deny(DeniedByUser);
        }

        switch (permission.Choice)
        {
            case PermissionChoice.AllowOnceChoice:
                return PreflightDecision.Allow;
            case PermissionChoice.AllowAlwaysChoice always:
                await RememberAsync(run, always.Scope, suggested, ctx, ct).ConfigureAwait(false);
                return PreflightDecision.Allow;
            case PermissionChoice.AllowEditedChoice edited:
                return await RecheckEditedAsync(run, call, edited.Input, ctx, ct).ConfigureAwait(false);
            case PermissionChoice.DenyChoice deny:
                return PreflightDecision.Deny(string.IsNullOrWhiteSpace(deny.Reason) ? DeniedByUser : $"{DeniedByUser} Reason given by the user: {deny.Reason}");
            default:
                return PreflightDecision.Deny(DeniedByUser);
        }
    }

    /// <summary>
    /// 「不在 Run 里」的一次调用的预检(<c>widgets.runAction</c>:用户点了 widget 里一颗预先声明的按钮)。走和 <see cref="DecideAsync"/> 同一张表——联网开关、deny 规则、PreToolUse 钩子 / 拦截器、ask / allow 规则——
    /// 但<b>不发起交互</b>:要问人时返回 <see cref="ActionPreflightKind.Ask"/>,由调用方把真实的工具与入参交给控制端确认(确认框由宿主画,widget 不能代答)。没有 AI 审核这一档。
    /// ★ 3.0 / 2.x 不变量:<b>非只读的动作第一次一定问</b>——哪怕档位是 <c>full</c>。widget 是模型写的,按钮背后是它预先写好的命令,用户此前没为它点过头;只有用户明确写过的 allow 规则才能免问。
    /// </summary>
    public async ValueTask<ActionPreflight> PreflightActionAsync(PermissionRunState run, ToolInvocation call, ILoopDiagnostics log, CancellationToken ct)
    {
        var info = call.Tool.Info;
        var traits = info.Traits;
        var target = await BuildTargetAsync(run, call.Input, ct).ConfigureAwait(false);
        var outcome = PermissionGate.Evaluate(new PermissionQuery(
            run.Mode, traits.ReadOnly, traits.Destructive, run.WebSearch, PermissionGate.NetworkSwitchTools.Contains(info.InternalId)));
        var rules = await _rules.ReadAsync(run.RuleContext, ct).ConfigureAwait(false);
        var early = PermissionDecisions.DecideBeforeHooks(outcome, PermissionRules.MatchFirst(rules.Deny, info.InternalId, target.Subjects.Loose));
        if (early.Denied)
        {
            return new ActionPreflight(ActionPreflightKind.Deny, early.Reason);
        }

        var verdict = await CollectVerdictAsync(new PreToolContext(run, call, call.Input), log, ct).ConfigureAwait(false);
        var allowRule = PermissionRules.MatchFirst(rules.Allow, info.InternalId, target.Subjects.Strict);
        var final = PermissionDecisions.DecideAfterHooks(
            outcome, verdict, PermissionRules.MatchFirst(rules.Ask, info.InternalId, target.Subjects.Loose), allowRule, autoReview: false);
        return final.Kind switch
        {
            FinalKind.Deny => new ActionPreflight(ActionPreflightKind.Deny, final.Reason),
            FinalKind.Allow when !traits.ReadOnly && allowRule is null => new ActionPreflight(ActionPreflightKind.Ask),
            FinalKind.Allow => new ActionPreflight(ActionPreflightKind.Allow),
            _ => new ActionPreflight(ActionPreflightKind.Ask),
        };
    }

    private const string DeniedByUser = "The user denied permission for this tool call. Do not retry it unchanged; ask the user how to proceed or take a different approach.";

    /// <summary>
    /// 「记住」:按作用域写进对应的存放处。落盘失败不该把用户刚点下的「允许」变成「拒绝」——这一次照常放行,只是没记住(记诊断)。
    /// </summary>
    private async ValueTask RememberAsync(PermissionRunState run, RememberScope scope, string rule, ExtensionContext ctx, CancellationToken ct)
    {
        try
        {
            var result = await _rules.AddAsync(scope, run.RuleContext, RuleBucket.Allow, rule, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                ctx.Log.Warn($"[permission] could not remember rule {rule} for scope {scope}: {result.Failure}");
            }
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            ctx.Log.Warn($"[permission] could not remember rule {rule} for scope {scope}.", e);
        }
    }

    /// <summary>
    /// <c>allow_edited</c>:用户改写了参数。<b>改写后的参数重新走一遍规则检查</b>(3.0 新增,2.x 没有,是个漏洞——改写可以把一条本来命中 deny 规则的参数,变成另一条不命中的,
    /// 或者反过来把工具指向一个 deny 规则保护的路径):联网开关、deny 规则、钩子 / 拦截器的拒绝都对新参数再判一次。用户已经明确批准了这条改写,所以不再问人。
    /// 工具自己的参数校验由循环层对 <see cref="PreflightDecision.Edit"/> 再过一遍。
    /// </summary>
    private async ValueTask<PreflightDecision> RecheckEditedAsync(PermissionRunState run, ToolInvocation call, JsonElement edited, ExtensionContext ctx, CancellationToken ct)
    {
        var info = call.Tool.Info;
        var target = await BuildTargetAsync(run, edited, ct).ConfigureAwait(false);
        var gate = PermissionGate.Evaluate(new PermissionQuery(
            run.Mode, info.Traits.ReadOnly, info.Traits.Destructive, run.WebSearch, PermissionGate.NetworkSwitchTools.Contains(info.InternalId)));
        var rules = await _rules.ReadAsync(run.RuleContext, ct).ConfigureAwait(false);
        var early = PermissionDecisions.DecideBeforeHooks(gate.Kind == GateKind.Deny ? gate : GateOutcome.Allow, PermissionRules.MatchFirst(rules.Deny, info.InternalId, target.Subjects.Loose));
        if (early.Denied)
        {
            return PreflightDecision.Deny($"The edited call is not permitted. {early.Reason}");
        }

        var verdict = await CollectVerdictAsync(new PreToolContext(run, call with { Input = edited }, edited), ctx.Log, ct).ConfigureAwait(false);
        return verdict.Deny is { } reason ? PreflightDecision.Deny(reason) : PreflightDecision.Edit(edited.Clone());
    }

    /// <summary>
    /// 钩子和插件拦截器并到同一次表决里。拦截器<b>只能收紧</b>(类型上没有 allow);任何一方失败 = 弃权并记诊断(Hook 的失败放行)。
    /// </summary>
    private async ValueTask<HookVerdict> CollectVerdictAsync(PreToolContext context, ILoopDiagnostics log, CancellationToken ct)
    {
        string? deny = null;
        var allow = false;
        var ask = false;
        foreach (var hook in _hooks)
        {
            try
            {
                var v = await hook.EvaluateAsync(context, ct).ConfigureAwait(false);
                deny ??= v.Deny;
                allow |= v.Allow;
                ask |= v.Ask;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                log.Warn("A PreToolUse hook failed; it abstains.", e);
            }
        }

        foreach (var interceptor in _interceptors)
        {
            try
            {
                var v = await interceptor.InterceptAsync(context, ct).ConfigureAwait(false);
                deny ??= v.Deny;
                ask |= v.Ask;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                log.Warn("A tool interceptor failed; it abstains.", e);
            }
        }

        return new HookVerdict(deny, allow, ask);
    }

    private async ValueTask<ReviewResult> ReviewAsync(PermissionRunState run, ToolInvocation call, CancellationToken ct)
    {
        if (_reviewer is null)
        {
            return ReviewResult.Unknown;
        }

        try
        {
            return await _reviewer.ReviewAsync(
                new ReviewRequest(run.RunId, run.SessionId, run.WorkspaceId, run.ReviewerModel, run.UserTask, call.Name, call.Input), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            _ = e; // 审核器挂了 ≠ 放行:回退人工审批
            return ReviewResult.Unknown;
        }
    }
}
