using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// 供应商调试弹窗(docs/design/07「供应商调试」,方案 A「调试台」):模型候选(已配置的在前、两族列表合并)、协议分段与最终地址、
/// 发送时的实时进度和完整记录、协议圆点、停止、调用失败、「使用这个协议」;以及编辑弹窗的「测试」测草稿、能调试的设备上「保存」不再自动测。
/// </summary>
public class ProviderDebugViewModelTests
{
    internal sealed class DebugCommands : IProviderEditorCommands, IProviderDebugCommands
    {
        public ProviderEditorViewModelTests.FakeCommands Editor { get; } = new();

        public List<ProviderDebugTargetDto> ModelTargets { get; } = [];

        public List<ProviderDebugRunParams> Runs { get; } = [];

        public ActionResult<ProviderDebugModelsResult> Models { get; set; } = new(ActionOutcome.Ok, new ProviderDebugModelsResult(
            [new DebugListAttemptDto("openai", "https://x/models", true, 200, 2, null, null), new DebugListAttemptDto("anthropic", "https://x/v1/models", false, 404, 0, "providers.fetchNotFound", null)],
            [new DebugModelDto("gpt-x", null, ["openai"]), new DebugModelDto("known-a", null, ["openai"])]));

        /// <summary>一次调试的脚本:拿到参数和进度回调,返回结果(可以先推几条进度、可以等取消)。</summary>
        public Func<ProviderDebugRunParams, Action<ProviderDebugEventDto>?, CancellationToken, Task<ActionResult<ProviderDebugResultDto>>> OnRun { get; set; } =
            (run, _, _) => Task.FromResult(new ActionResult<ProviderDebugResultDto>(ActionOutcome.Ok, Ok(run)));

        public bool SupportsDebug(string deviceId) => true;

        public Task<ActionResult<ProviderDto>> UpsertAsync(string deviceId, ProvidersUpsertParams provider, CancellationToken ct = default) => Editor.UpsertAsync(deviceId, provider, ct);

        public Task<ActionResult<CredentialInfoDto>> SetApiKeyAsync(string deviceId, string providerId, string apiKey, CancellationToken ct = default) => Editor.SetApiKeyAsync(deviceId, providerId, apiKey, ct);

        public Task<ActionResult<ProviderTestResultDto>> TestAsync(string deviceId, string providerId, CancellationToken ct = default) => Editor.TestAsync(deviceId, providerId, ct);

        public Task<ActionResult<ProviderDebugModelsResult>> DebugModelsAsync(string deviceId, ProviderDebugTargetDto target, CancellationToken ct = default)
        {
            ModelTargets.Add(target);
            return Task.FromResult(Models);
        }

        public Task<ActionResult<ProviderDebugResultDto>> DebugRunAsync(string deviceId, ProviderDebugRunParams run, Action<ProviderDebugEventDto>? onEvent = null, CancellationToken ct = default)
        {
            Runs.Add(run);
            return OnRun(run, onEvent, ct);
        }
    }

    private static ProviderDebugEventDto Ev(int seq, string kind, string? name, string data) => new(seq, seq * 10, kind, name, data);

    internal static ProviderDebugResultDto Ok(ProviderDebugRunParams run, string text = "OK") => new(
        run.RunId, true, run.Protocol, run.Model, "POST", "https://x/v1/chat/completions",
        [new DebugHeaderDto("authorization", "Bearer ••••abcd")], "{}", 200, [new DebugHeaderDto("content-type", "text/event-stream")], null,
        [Ev(1, DebugEventKinds.Request, "https://x/v1/chat/completions", "{}"), Ev(2, DebugEventKinds.Response, "200", ""), Ev(3, DebugEventKinds.Text, null, text)],
        false, text, "", [], "endTurn", new DebugUsageDto(3, 1, null, null, null), run.Model, 30, 40, null, null, null);

    internal static ProviderDebugResultDto Failed(ProviderDebugRunParams run, int? status, string failure) => new(
        run.RunId, false, run.Protocol, run.Model, "POST", "https://x/v1/chat/completions",
        [], "{}", status, [], status is null ? null : """{"error":"nope"}""", [Ev(1, DebugEventKinds.Error, failure, "nope")],
        false, "", "", [], null, null, null, null, status is null ? null : 50, failure, "provider", "nope");

    private static ProviderDebugViewModel New(DebugCommands commands, string protocol = "openai-chat", IReadOnlyList<string>? known = null, bool canApply = false) =>
        new(commands, "dev", new ProviderDebugTargetDto("gw"), "GW", "https://x/v1", protocol, known, canApply);

    [Fact]
    public async Task Candidates_put_the_configured_models_first_and_add_the_upstream_ones_once()
    {
        var commands = new DebugCommands();
        var vm = New(commands, known: ["known-a", "known-b"]);
        Assert.Equal("known-a", vm.Model); // 打开就有个可以直接发的模型

        await vm.LoadModelsAsync();

        Assert.Equal(["known-a", "known-b", "gpt-x"], vm.ModelCandidates.ToArray());
        Assert.Equal(2, vm.FetchedCount);
        Assert.False(vm.ModelsUnavailable); // 一族拉到了就不算拉不到
        Assert.Equal("gw", Assert.Single(commands.ModelTargets).ProviderId);
    }

    [Fact]
    public async Task When_no_family_answers_the_dialog_asks_for_a_typed_model_and_a_failed_call_shows_its_reason()
    {
        var commands = new DebugCommands
        {
            Models = new(ActionOutcome.Ok, new ProviderDebugModelsResult(
                [new DebugListAttemptDto("openai", "u", false, 401, 0, "providers.fetchRejected", null), new DebugListAttemptDto("anthropic", "u", false, 404, 0, "providers.fetchNotFound", null)], [])),
        };
        var vm = New(commands);
        await vm.LoadModelsAsync();
        Assert.True(vm.ModelsUnavailable);
        Assert.Equal("", vm.Model);
        Assert.False(vm.SendCommand.CanExecute(null)); // 没有模型发不了

        commands.Models = new(ActionOutcome.Unavailable);
        await vm.LoadModelsAsync();
        Assert.Equal("Unavailable", vm.ModelsErrorKey);
    }

    [Fact]
    public void The_request_url_follows_the_selected_protocol_with_the_same_rule_as_the_device()
    {
        var vm = new ProviderDebugViewModel(new DebugCommands(), "dev", new ProviderDebugTargetDto("an"), "AN", " https://relay.example.com/v1 ", "anthropic");
        Assert.Equal("https://relay.example.com/v1/messages", vm.FinalUrl); // Anthropic 去重 /v1
        Assert.True(vm.Protocols.Single(p => p.Protocol == "anthropic").IsSelected);

        vm.SelectProtocolCommand.Execute("openai-responses");
        Assert.Equal("https://relay.example.com/v1/responses", vm.FinalUrl);
        Assert.Equal(["openai-responses"], vm.Protocols.Where(p => p.IsSelected).Select(p => p.Protocol).ToArray());

        vm.SelectProtocolCommand.Execute("bogus");
        Assert.Equal("openai-responses", vm.SelectedProtocol);
    }

    [Fact]
    public async Task Sending_streams_progress_then_takes_the_full_record_and_marks_the_protocol()
    {
        var commands = new DebugCommands();
        var vm = New(commands, known: ["  gpt-x  "]);
        vm.Model = "  gpt-x  ";
        vm.Thinking = DebugThinking.Off;
        vm.MaxOutputTokens = 256;
        vm.WithTool = true;
        var seenWhileRunning = "";
        commands.OnRun = async (run, onEvent, _) =>
        {
            onEvent!(Ev(1, DebugEventKinds.Request, "https://x", "{}"));
            onEvent(Ev(2, DebugEventKinds.Response, "200", ""));
            onEvent(Ev(3, DebugEventKinds.Text, null, "O"));
            onEvent(Ev(3, DebugEventKinds.Text, null, "O")); // 快照和事件里给了同一条:按序号去重
            onEvent(Ev(4, DebugEventKinds.Text, null, "K"));
            seenWhileRunning = vm.ReplyText;
            Assert.True(vm.IsRunning);
            Assert.Equal(DebugProtocolState.Running, vm.Protocols.Single(p => p.Protocol == "openai-chat").State);
            Assert.Equal(200, vm.StatusCode);
            await Task.Yield();
            return new ActionResult<ProviderDebugResultDto>(ActionOutcome.Ok, Ok(run, "OK!"));
        };

        await vm.SendCommand.ExecuteAsync(null);

        var run = Assert.Single(commands.Runs);
        Assert.Equal(("gw", "openai-chat", "gpt-x", DebugThinking.Off, 256, true), (run.Target.ProviderId, run.Protocol, run.Model, run.Thinking, run.MaxOutputTokens, run.WithTool));
        Assert.Equal("OK", seenWhileRunning);
        Assert.Equal("OK!", vm.ReplyText); // 完整记录是权威的
        Assert.Equal(3, vm.Events.Count);
        Assert.True(vm.IsOk);
        Assert.False(vm.IsRunning);
        Assert.Equal(DebugProtocolState.Ok, vm.Protocols.Single(p => p.Protocol == "openai-chat").State);
        Assert.Equal(DebugProtocolState.Untested, vm.Protocols.Single(p => p.Protocol == "anthropic").State);
        Assert.StartsWith("POST https://x/v1/chat/completions\nauthorization: Bearer ••••abcd", vm.RequestText.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.StartsWith("HTTP 200", vm.ResponseText);
    }

    [Fact]
    public async Task A_failed_request_marks_the_protocol_red_and_opens_the_response_tab_and_late_progress_is_ignored()
    {
        var commands = new DebugCommands();
        var vm = New(commands, known: ["m"]);
        Action<ProviderDebugEventDto>? late = null;
        commands.OnRun = (run, onEvent, _) =>
        {
            late = onEvent;
            return Task.FromResult(new ActionResult<ProviderDebugResultDto>(ActionOutcome.Ok, Failed(run, 404, DebugFailures.NotFound)));
        };

        await vm.SendCommand.ExecuteAsync(null);
        late!(Ev(9, DebugEventKinds.Text, null, "stale"));

        Assert.True(vm.IsFailed);
        Assert.Equal(DebugProtocolState.Failed, vm.Protocols.Single(p => p.IsSelected).State);
        Assert.Equal(DebugTab.Response, vm.SelectedTab);
        Assert.Equal("", vm.ReplyText);
        Assert.Single(vm.Events);
        Assert.Contains("{\"error\":\"nope\"}", vm.ResponseText);
    }

    [Fact]
    public async Task Stopping_cancels_the_call_keeps_what_arrived_and_restores_the_dot()
    {
        var commands = new DebugCommands();
        var vm = New(commands, known: ["m"]);
        var started = new TaskCompletionSource();
        commands.OnRun = async (_, onEvent, ct) =>
        {
            onEvent!(Ev(1, DebugEventKinds.Request, "u", "{}"));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };

        var sending = vm.SendCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.StopCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        await sending;

        Assert.True(vm.WasStopped);
        Assert.False(vm.IsRunning);
        Assert.Null(vm.Result);
        Assert.Single(vm.Events);
        Assert.Equal(DebugProtocolState.Untested, vm.Protocols.Single(p => p.IsSelected).State);
    }

    [Fact]
    public async Task A_call_that_never_reached_the_upstream_shows_its_reason_and_keeps_the_previous_dot()
    {
        var commands = new DebugCommands();
        var vm = New(commands, known: ["m"]);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(DebugProtocolState.Ok, vm.Protocols.Single(p => p.IsSelected).State);

        commands.OnRun = (_, _, _) => Task.FromResult(new ActionResult<ProviderDebugResultDto>(ActionOutcome.Invalid, ErrorKey: "providers.debug.modelRequired"));
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("providers.debug.modelRequired", vm.RunErrorKey);
        Assert.True(vm.HasRunError);
        Assert.Equal(DebugProtocolState.Ok, vm.Protocols.Single(p => p.IsSelected).State);
    }

    [Fact]
    public async Task Use_this_protocol_appears_only_from_the_editor_after_another_protocol_works()
    {
        var commands = new DebugCommands();
        var fromDetail = New(commands, known: ["m"]);
        fromDetail.SelectProtocolCommand.Execute("anthropic");
        await fromDetail.SendCommand.ExecuteAsync(null);
        Assert.False(fromDetail.CanUseProtocol); // 详情页打开的不做应用

        var vm = New(commands, protocol: "openai-chat", known: ["m"], canApply: true);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.False(vm.CanUseProtocol); // 跑通的就是草稿本来的协议

        vm.SelectProtocolCommand.Execute("anthropic");
        Assert.False(vm.CanUseProtocol); // 还没在这个协议上跑通
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.CanUseProtocol);

        var closed = 0;
        vm.Completed += () => closed++;
        vm.UseProtocolCommand.Execute(null);
        Assert.Equal("anthropic", vm.AppliedProtocol);
        Assert.Equal(1, closed);
    }

    // ── 编辑弹窗:「测试」测草稿,「保存」不再自动测 ──

    private static ProviderEditorViewModel Editor(DebugCommands commands, Func<ProviderDebugViewModel, Task>? show) =>
        new(commands, "dev", ProviderEditorViewModelTests.Templates, [], 0) { ShowDebug = show };

    [Fact]
    public async Task On_a_device_that_can_debug_saving_just_saves_and_test_opens_the_debugger_on_the_draft()
    {
        var commands = new DebugCommands();
        ProviderDebugViewModel? shown = null;
        var vm = Editor(commands, form =>
        {
            shown = form;
            return Task.CompletedTask;
        });
        vm.PickCommand.Execute(vm.Groups.SelectMany(g => g.Items).First(i => i.Id == "deepseek-anthropic"));
        vm.ApiKey = " sk-draft-1234 ";
        Assert.True(vm.DebugEnabled);
        Assert.False(vm.SaveTests);
        Assert.True(vm.CanTest);

        await vm.TestCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal(new ProviderDebugTargetDto(null, "https://api.deepseek.com/anthropic", "anthropic", "sk-draft-1234", "text"), GetTarget(commands, shown!));
        Assert.Equal("anthropic", shown!.SelectedProtocol);
        Assert.Empty(commands.Editor.Upserts); // 测试不写任何东西

        var closed = 0;
        vm.Completed += () => closed++;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(commands.Editor.Upserts);
        Assert.Empty(commands.Editor.Tests); // 不再自动测连接
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task A_protocol_chosen_in_the_debugger_is_written_back_to_the_draft()
    {
        var commands = new DebugCommands();
        var vm = Editor(commands, async form =>
        {
            form.Model = "m";
            form.SelectProtocolCommand.Execute("openai-chat");
            await form.SendCommand.ExecuteAsync(null);
            form.UseProtocolCommand.Execute(null);
        });
        vm.PickCommand.Execute(vm.Groups.SelectMany(g => g.Items).First(i => i.Id == "deepseek-anthropic"));

        await vm.TestCommand.ExecuteAsync(null);

        Assert.Equal("openai-chat", vm.Protocol);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Without_a_debugger_host_the_editor_keeps_save_and_test()
    {
        var vm = Editor(new DebugCommands(), show: null);
        vm.PickCommand.Execute(vm.Groups.SelectMany(g => g.Items).First(i => i.Id == "deepseek"));
        Assert.False(vm.DebugEnabled);
        Assert.True(vm.SaveTests);
        Assert.False(vm.CanTest);
    }

    /// <summary>调试弹窗拿到的调试对象(拉一次候选就知道它带的是什么)。</summary>
    private static ProviderDebugTargetDto GetTarget(DebugCommands commands, ProviderDebugViewModel form)
    {
        commands.ModelTargets.Clear();
        form.LoadModelsAsync().GetAwaiter().GetResult();
        return Assert.Single(commands.ModelTargets);
    }

    [Fact]
    public void Media_provider_debugger_shows_only_matching_protocols_and_builds_volcengine_media_urls()
    {
        var imgVm = new ProviderDebugViewModel(
            new DebugCommands(),
            "dev",
            new ProviderDebugTargetDto("ark-img", Kind: "image"),
            "火山图片",
            "https://ark.cn-beijing.volces.com/api/v3",
            "volcengine-images",
            kind: "image");

        Assert.False(imgVm.IsTextKind);
        Assert.Equal(["openai-images", "volcengine-images"], imgVm.Protocols.Select(p => p.Protocol).ToArray());
        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/images/generations", imgVm.FinalUrl);

        var vidVm = new ProviderDebugViewModel(
            new DebugCommands(),
            "dev",
            new ProviderDebugTargetDto("ark-vid", Kind: "video"),
            "火山视频",
            "https://ark.cn-beijing.volces.com/api/v3",
            "volcengine-videos",
            kind: "video");

        Assert.False(vidVm.IsTextKind);
        Assert.Equal(["openai-videos", "volcengine-videos"], vidVm.Protocols.Select(p => p.Protocol).ToArray());
        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks", vidVm.FinalUrl);
    }
}
