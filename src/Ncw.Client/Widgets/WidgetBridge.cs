using System.Collections.Immutable;
using System.Text;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Core.Domain;

namespace Ncw.Client.Widgets;

/// <summary>需要用户点头的两件事的确认框(界面层按当前语言画;确认框里显示<b>真实</b>的地址 / 工具与入参,不是小组件自己写的文案)。默认全部拒绝。</summary>
public interface IWidgetConfirmations
{
    Task<bool> ConfirmOpenUrlAsync(string url);

    Task<bool> ConfirmActionAsync(WidgetActionPrompt prompt);
}

public sealed class NoWidgetConfirmations : IWidgetConfirmations
{
    public static readonly NoWidgetConfirmations Instance = new();

    public Task<bool> ConfirmOpenUrlAsync(string url) => Task.FromResult(false);

    public Task<bool> ConfirmActionAsync(WidgetActionPrompt prompt) => Task.FromResult(false);
}

/// <summary>桥此刻需要知道的(每次调用现读,不缓存:提交窗口、设备连接随时在变)。</summary>
public sealed record WidgetBridgeContext(WidgetFacts Facts, ImmutableArray<WidgetActionInfo> Actions);

/// <summary>
/// 一张卡的宿主桥:接 WebView 页面发来的消息(<see cref="HandleAsync"/>),校验、限速,再调 <see cref="WidgetActions"/>(规则 4:提交 / 动作 / 状态的意图只在 Actions 里),把结果经 <c>post</c> 回给页面。
/// 不依赖任何 UI 或 WebView 类型,所以恶意载荷(垃圾 / 超大 / 伪造类型的消息)可以直接在单测里喂进来。
/// <para>防护:① 只认已知消息类型和桥方法(<see cref="WidgetInboundParser"/>);② 全局限速 + 调用限速;③ 累计违规 <see cref="MaxStrikes"/> 次就锁死,之后一切消息丢弃(页面坏了或者在攻击,
/// 不值得再陪它);④ 同一时刻只有一个确认框,第二个直接拒绝;⑤ <c>openUrl</c> / 链接点击都要确认,且只有 http(s);⑥ 动作的「要跑什么」不由页面决定。</para>
/// </summary>
public sealed class WidgetBridge
{
    public const int MaxStrikes = 100;

    private readonly WidgetRef _card;
    private readonly WidgetActions _actions;
    private readonly IWidgetConfirmations _confirmations;
    private readonly Func<IUrlLauncher> _launcher;
    private readonly Func<WidgetBridgeContext> _context;
    private readonly Action<string> _post;
    private readonly WidgetRateLimiter _inbound;
    private readonly WidgetRateLimiter _calls;
    private readonly WidgetRateLimiter _wheel;
    private readonly SemaphoreSlim _confirming = new(1, 1);
    private int _strikes;

    public WidgetBridge(WidgetRef card, WidgetActions actions, IWidgetConfirmations confirmations, Func<IUrlLauncher> launcher, Func<WidgetBridgeContext> context, Action<string> post, TimeProvider? clock = null)
    {
        _card = card;
        _actions = actions;
        _confirmations = confirmations;
        _launcher = launcher;
        _context = context;
        _post = post;
        clock ??= TimeProvider.System;
        // 全部消息(含报高:ResizeObserver 经 rAF 合并,最多每帧一条);调用单独再限一道
        _inbound = new WidgetRateLimiter(clock, burst: 120, perSecond: 60);
        _calls = new WidgetRateLimiter(clock, burst: 12, perSecond: 6);
        // 滚轮单独一个桶,不占上面的全局额度:外壳把同一批事件合成一条,触控板 / 高精度滚轮一秒一百来条;滚得再快也不该把报高挤掉、更不该攒违规把桥锁死
        _wheel = new WidgetRateLimiter(clock, burst: 240, perSecond: 240);
    }

    /// <summary>页面发出「就绪」(控制端据此推送主题 / 环境 / 内容)。</summary>
    public event Action? Ready;

    /// <summary>页面报了高度(已钳制到 <see cref="WidgetLimits"/> 的区间)。</summary>
    public event Action<double>? HeightChanged;

    /// <summary>页面没有消费掉的滚轮位移(CSS 像素,正 = 往下,已校验有界);界面层拿去滚动外面的转录。在调用 <see cref="HandleAsync"/> 的线程上同步触发。</summary>
    public event Action<double>? Wheel;

    /// <summary>违规太多,桥已锁死(只触发一次)。</summary>
    public event Action? Locked;

    public bool IsLocked { get; private set; }

    public int Strikes => _strikes;

    /// <summary>处理页面发来的一条原始消息。<b>从不抛</b>。</summary>
    public async Task HandleAsync(string? body)
    {
        if (IsLocked)
        {
            return;
        }

        // 滚轮的快速通道:只有短消息才先解析(解析的代价有上限),是滚轮就走自己的桶;超额的滚轮直接丢掉、不算违规
        // (最坏是页面自己在滚外面的转录——它能做的也就这么多,碰不到任何状态)
        var message = body is { Length: <= WidgetLimits.MaxWheelMessageChars } ? WidgetInboundParser.Parse(body) : null;
        if (message is WidgetWheel wheel)
        {
            if (_wheel.TryTake())
            {
                Wheel?.Invoke(wheel.DeltaY);
            }

            return;
        }

        if (!_inbound.TryTake())
        {
            Strike();
            return;
        }

        message ??= WidgetInboundParser.Parse(body);
        switch (message)
        {
            case WidgetReady:
                Ready?.Invoke();
                return;
            case WidgetHeight h:
                HeightChanged?.Invoke(WidgetLimits.ClampHeight(h.Value));
                return;
            case WidgetRejected r:
                Strike();
                if (r.Id is { } rejectedId && !IsLocked && _calls.TryTake())
                {
                    _post(WidgetPush.Reply(rejectedId, false, error: r.Reason));
                }

                return;
            case WidgetBridgeCall call:
                if (!_calls.TryTake())
                {
                    Strike();
                    _post(WidgetPush.Reply(call.Id, false, error: "rate_limited"));
                    return;
                }

                await DispatchAsync(call).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// 在系统浏览器里打开一个外链:<c>openUrl</c> 调用和被拦下的链接点击 / 新窗口请求走同一条路(只有 http(s),先确认、确认框里是真实地址)。
    /// </summary>
    public async Task<bool> OpenExternalAsync(Uri url)
    {
        if (!WidgetInboundParser.TryExternalUrl(url.OriginalString, out var safe))
        {
            return false;
        }

        if (!await _confirming.WaitAsync(0).ConfigureAwait(false))
        {
            return false; // 已经有一个确认框了:不排队
        }

        try
        {
            if (!await _confirmations.ConfirmOpenUrlAsync(safe.AbsoluteUri).ConfigureAwait(false))
            {
                return false;
            }
        }
        finally
        {
            _confirming.Release();
        }

        await _launcher().OpenAsync(safe.AbsoluteUri).ConfigureAwait(false);
        return true;
    }

    private void Strike()
    {
        if (Interlocked.Increment(ref _strikes) >= MaxStrikes && !IsLocked)
        {
            IsLocked = true;
            Locked?.Invoke();
        }
    }

    private async Task DispatchAsync(WidgetBridgeCall call)
    {
        string reply;
        try
        {
            reply = call switch
            {
                SetStateCall s => await SetStateAsync(s).ConfigureAwait(false),
                SubmitCall s => await SubmitAsync(s).ConfigureAwait(false),
                RunActionCall a => await RunActionAsync(a).ConfigureAwait(false),
                OpenUrlCall u => await OpenUrlAsync(u).ConfigureAwait(false),
                _ => WidgetPush.Reply(call.Id, false, error: "unsupported"),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            reply = WidgetPush.Reply(call.Id, false, error: "failed");
        }

        _post(reply);
    }

    private async Task<string> SetStateAsync(SetStateCall call)
    {
        var context = _context();
        if (!WidgetView.CanCallDevice(context.Facts))
        {
            return Fail(call.Id, context.Facts.DeviceUnknown ? "offline" : "unavailable");
        }

        var result = await _actions.SetStateAsync(_card.DeviceId, _card.SessionId, _card.CallId, call.StateJson).ConfigureAwait(false);
        return result.IsOk ? WidgetPush.Reply(call.Id, true, "true") : Fail(call.Id, Map(result.Outcome));
    }

    private async Task<string> SubmitAsync(SubmitCall call)
    {
        var facts = _context().Facts;
        if (!facts.Interactive)
        {
            return Fail(call.Id, "not_interactive");
        }

        if (facts.Submitted)
        {
            return Fail(call.Id, "already_submitted");
        }

        if (!WidgetView.CanSubmit(facts))
        {
            return Fail(call.Id, facts.DeviceUnknown ? "offline" : "closed");
        }

        var result = await _actions.SubmitAsync(_card.DeviceId, _card.SessionId, _card.CallId, call.Value).ConfigureAwait(false);
        if (result.IsOk)
        {
            return result.Value!.Accepted ? WidgetPush.Reply(call.Id, true, "true") : Fail(call.Id, "closed");
        }

        return Fail(call.Id, result.Outcome is ActionOutcome.NotFound or ActionOutcome.Conflict ? "closed" : Map(result.Outcome));
    }

    private async Task<string> RunActionAsync(RunActionCall call)
    {
        var context = _context();
        if (!WidgetView.CanCallDevice(context.Facts))
        {
            return Fail(call.Id, context.Facts.DeviceUnknown ? "offline" : "unavailable");
        }

        var label = context.Actions.FirstOrDefault(a => a.Id == call.ActionId)?.Label ?? call.ActionId;
        var outcome = await _actions.RunDeclaredActionAsync(_card.DeviceId, _card.SessionId, _card.CallId, call.ActionId, label, call.ParamsJson, ConfirmActionAsync).ConfigureAwait(false);
        return outcome.Kind switch
        {
            WidgetActionKind.Result => WidgetPush.Reply(call.Id, true, ActionResultJson(outcome.Ok, outcome.Output)),
            WidgetActionKind.Denied => Fail(call.Id, outcome.Reason ?? "denied"),
            WidgetActionKind.Declined => Fail(call.Id, "declined"),
            _ => Fail(call.Id, outcome.Reason == nameof(ActionOutcome.Unavailable) ? "offline" : "failed"),
        };
    }

    private async Task<bool> ConfirmActionAsync(WidgetActionPrompt prompt)
    {
        if (!await _confirming.WaitAsync(0).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            return await _confirmations.ConfirmActionAsync(prompt).ConfigureAwait(false);
        }
        finally
        {
            _confirming.Release();
        }
    }

    private async Task<string> OpenUrlAsync(OpenUrlCall call) =>
        await OpenExternalAsync(call.Url).ConfigureAwait(false) ? WidgetPush.Reply(call.Id, true, "true") : Fail(call.Id, "declined");

    private static string Fail(int id, string error) => WidgetPush.Reply(id, false, error: error);

    private static string Map(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Forbidden => "forbidden",
        ActionOutcome.Invalid => "invalid",
        ActionOutcome.NotFound => "not_found",
        ActionOutcome.Unavailable => "offline",
        ActionOutcome.Unsupported => "unsupported",
        _ => "failed",
    };

    /// <summary><c>runAction</c> 的返回值:<c>{ok, output}</c>(输出是工具的文本,不进模型上下文)。</summary>
    private static string ActionResultJson(bool ok, string? output)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", ok);
            w.WriteString("output", output ?? "");
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
