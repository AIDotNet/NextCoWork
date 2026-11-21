using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Controls;
using Ncw.UI.Views.Dialogs;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 供应商调试弹窗(docs/design/07「供应商调试」,方案 A「调试台」):宽 900、打开就拉模型候选、发送后四个标签页、失败时状态行与说明、
/// 叠在「添加供应商」弹窗上面打开并把协议写回草稿、窄窗口收窄。截图在 <c>screenshots/provider-debug-*.png</c>。
/// </summary>
public class ProviderDebugDialogTests
{
    private sealed class FakeCommands : IProviderEditorCommands, IProviderDebugCommands
    {
        public Func<ProviderDebugRunParams, ProviderDebugResultDto> OnRun { get; set; } = Success;

        public int ModelCalls { get; private set; }

        public bool SupportsDebug(string deviceId) => true;

        public Task<ActionResult<ProviderDto>> UpsertAsync(string deviceId, ProvidersUpsertParams provider, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<ProviderDto>(ActionOutcome.Ok));

        public Task<ActionResult<CredentialInfoDto>> SetApiKeyAsync(string deviceId, string providerId, string apiKey, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<CredentialInfoDto>(ActionOutcome.Ok, new CredentialInfoDto(true, apiKey[^4..], null, null, null)));

        public Task<ActionResult<ProviderTestResultDto>> TestAsync(string deviceId, string providerId, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<ProviderTestResultDto>(ActionOutcome.Ok, new ProviderTestResultDto(true, 90, 200, null, null)));

        public Task<ActionResult<ProviderDebugModelsResult>> DebugModelsAsync(string deviceId, ProviderDebugTargetDto target, CancellationToken ct = default)
        {
            ModelCalls++;
            return Task.FromResult(new ActionResult<ProviderDebugModelsResult>(ActionOutcome.Ok, new ProviderDebugModelsResult(
                [
                    new DebugListAttemptDto("anthropic", "https://api.deepseek.com/anthropic/v1/models?limit=1000", false, 404, 0, "providers.fetchNotFound", null),
                    new DebugListAttemptDto("openai", "https://api.deepseek.com/anthropic/models", true, 200, 2, null, null),
                ],
                [new DebugModelDto("deepseek-chat", null, ["openai"]), new DebugModelDto("deepseek-reasoner", null, ["openai"])])));
        }

        public Task<ActionResult<ProviderDebugResultDto>> DebugRunAsync(string deviceId, ProviderDebugRunParams run, Action<ProviderDebugEventDto>? onEvent = null, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<ProviderDebugResultDto>(ActionOutcome.Ok, OnRun(run)));
    }

    private static ProviderDebugEventDto Ev(int seq, int ms, string kind, string? name, string data) => new(seq, ms, kind, name, data);

    private static ProviderDebugResultDto Success(ProviderDebugRunParams run) => new(
        run.RunId, true, run.Protocol, run.Model, "POST", "https://api.deepseek.com/anthropic/v1/messages",
        [new DebugHeaderDto("user-agent", "NextCoWork/3.x"), new DebugHeaderDto("anthropic-version", "2023-06-01"), new DebugHeaderDto("x-api-key", "••••a1b2"),
            new DebugHeaderDto("accept", "text/event-stream"), new DebugHeaderDto("content-type", "application/json; charset=utf-8")],
        "{\n  \"model\": \"deepseek-chat\",\n  \"max_tokens\": 1024,\n  \"stream\": true,\n  \"messages\": [\n    {\n      \"role\": \"user\",\n      \"content\": \"Reply with the two letters OK.\"\n    }\n  ]\n}",
        200,
        [new DebugHeaderDto("content-type", "text/event-stream"), new DebugHeaderDto("x-request-id", "req_9f3a")],
        null,
        [
            Ev(1, 0, DebugEventKinds.Request, "https://api.deepseek.com/anthropic/v1/messages", "{…}"),
            Ev(2, 398, DebugEventKinds.Response, "200", "content-type: text/event-stream"),
            Ev(3, 412, DebugEventKinds.Sse, "message_start", """{"type":"message_start","message":{"id":"msg_01","model":"deepseek-chat","usage":{"input_tokens":12}}}"""),
            Ev(4, 413, DebugEventKinds.Sse, "content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Ev(5, 418, DebugEventKinds.Sse, "content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"OK"}}"""),
            Ev(6, 418, DebugEventKinds.Text, null, "OK"),
            Ev(7, 1290, DebugEventKinds.Sse, "content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Ev(8, 1301, DebugEventKinds.Sse, "message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}"""),
            Ev(9, 1305, DebugEventKinds.Sse, "message_stop", """{"type":"message_stop"}"""),
            Ev(10, 1305, DebugEventKinds.End, "endTurn", "input=12 output=3"),
        ],
        false, "OK", "", [], "endTurn", new DebugUsageDto(12, 3, null, null, null), "deepseek-chat", 412, 1310, null, null, null);

    private static ProviderDebugResultDto NotFound(ProviderDebugRunParams run) => new(
        run.RunId, false, run.Protocol, run.Model, "POST", "https://api.deepseek.com/anthropic/chat/completions",
        [new DebugHeaderDto("authorization", "Bearer ••••a1b2")], "{}", 404, [new DebugHeaderDto("content-type", "application/json")],
        """{"error":{"message":"Not Found","type":"invalid_request_error"}}""",
        [Ev(1, 0, DebugEventKinds.Request, "https://api.deepseek.com/anthropic/chat/completions", "{}"), Ev(2, 220, DebugEventKinds.Response, "404", ""),
            Ev(3, 221, DebugEventKinds.Error, DebugFailures.NotFound, "Not Found")],
        false, "", "", [], null, null, null, null, 221, DebugFailures.NotFound, "provider", "Not Found");

    private static void Dispatch()
    {
        for (var i = 0; i < 8; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(Window window, string name)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static (Window Window, NcwDialogService Dialogs) Host(bool dark, double width = 1180, double height = 820)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        var root = new Border { Background = Avalonia.Media.Brushes.Transparent };
        var window = new Window { Width = width, Height = height, Content = root };
        window.Styles.Add(new Ncw.UI.Themes.NcwStyles());
        window.Show();
        Dispatch();
        return (window, new NcwDialogService(root));
    }

    private static ProviderDebugViewModel Form(FakeCommands commands, bool canApply = false) =>
        new(commands, "dev", new ProviderDebugTargetDto("deepseek-anthropic"), "DeepSeek", "https://api.deepseek.com/anthropic", "anthropic", canApplyProtocol: canApply);

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_debugger_is_900_wide_loads_candidates_and_shows_every_tab_after_a_send(bool dark)
    {
        var (window, dialogs) = Host(dark);
        var theme = dark ? "dark" : "light";
        try
        {
            var commands = new FakeCommands();
            var form = Form(commands);
            var shown = dialogs.ShowProviderDebugAsync(form);
            Dispatch();

            var content = window.GetVisualDescendants().OfType<ProviderDebugDialog>().Single();
            var surface = content.GetVisualAncestors().OfType<DialogSurface>().First();
            Assert.Equal(NcwDialogService.ProviderDebugWidth, surface.Bounds.Width, 0.5);
            Assert.Equal(1, commands.ModelCalls); // 打开就拉一次候选
            Assert.Equal("deepseek-chat", form.Model);
            Assert.Equal("https://api.deepseek.com/anthropic/v1/messages", Named<SelectableTextBlock>(window, "UrlText").Text);
            Assert.True(Named<TextBlock>(window, "PlaceholderText").IsEffectivelyVisible);
            Snapshot(window, $"provider-debug-empty-{theme}");

            await form.SendCommand.ExecuteAsync(null);
            Dispatch();
            Assert.True(form.IsOk);
            Assert.False(Named<TextBlock>(window, "PlaceholderText").IsEffectivelyVisible);
            Assert.Equal("OK", Named<SelectableTextBlock>(window, "ReplyText").Text);
            Assert.True(Named<WrapPanel>(window, "StatusRow").Children.Count >= 4); // 状态、首字、总耗时、token、停止原因
            Assert.Equal(DebugProtocolState.Ok, form.Protocols.Single(p => p.IsSelected).State);
            Snapshot(window, $"provider-debug-reply-{theme}");

            form.SelectTabCommand.Execute(DebugTab.Events);
            Dispatch();
            Assert.True(Named<Grid>(window, "EventsPane").IsEffectivelyVisible);
            Assert.False(Named<ScrollViewer>(window, "ReplyPane").IsEffectivelyVisible);
            Snapshot(window, $"provider-debug-events-{theme}");

            form.SelectTabCommand.Execute(DebugTab.Request);
            Dispatch();
            Assert.True(Named<ScrollViewer>(window, "RequestPane").IsEffectivelyVisible);
            Snapshot(window, $"provider-debug-request-{theme}");

            // 换一个协议,404:状态行变红,说明里带修法,停在「回复」时自动切到「响应」(用户自己选的别的标签页不动)
            form.SelectTabCommand.Execute(DebugTab.Reply);
            commands.OnRun = NotFound;
            form.SelectProtocolCommand.Execute("openai-chat");
            await form.SendCommand.ExecuteAsync(null);
            Dispatch();
            Assert.Equal(DebugTab.Response, form.SelectedTab);
            var failure = Named<TextBlock>(window, "FailureLine");
            Assert.True(failure.IsEffectivelyVisible);
            Assert.StartsWith(ProviderDebugDialog.FailureText(DebugFailures.NotFound), failure.Text, StringComparison.Ordinal);
            Snapshot(window, $"provider-debug-failed-{theme}");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            await shown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Opened_from_the_editor_it_stacks_on_top_and_writes_the_working_protocol_back_to_the_draft()
    {
        var (window, dialogs) = Host(dark: false);
        try
        {
            var commands = new FakeCommands();
            var editor = new ProviderEditorViewModel(commands, "dev", [
                new ProviderTemplateDto("deepseek-anthropic", "deepseek", "DeepSeek", ProviderTemplateGroups.China, ProviderTemplateVariants.Anthropic, "anthropic",
                    "https://api.deepseek.com/anthropic", "https://platform.deepseek.com/api_keys", true, true),
            ], [])
            {
                ShowDebug = dialogs.ShowProviderDebugAsync,
            };
            var editing = dialogs.ShowProviderEditorAsync(editor);
            Dispatch();
            editor.PickCommand.Execute(editor.Groups[0].Items[0]);
            editor.ApiKey = "sk-draft-a1b2";
            Dispatch();
            Assert.True(Named<NcwButton>(window, "TestButton").IsEffectivelyVisible);

            var testing = editor.TestCommand.ExecuteAsync(null);
            Dispatch();
            Assert.Single(window.GetVisualDescendants().OfType<ProviderDebugDialog>());
            Assert.Single(window.GetVisualDescendants().OfType<ProviderEditorDialog>()); // 编辑弹窗还在下面
            var form = (ProviderDebugViewModel)window.GetVisualDescendants().OfType<ProviderDebugDialog>().Single().DataContext!;
            form.SelectProtocolCommand.Execute("openai-chat");
            await form.SendCommand.ExecuteAsync(null);
            Dispatch();
            Assert.True(form.CanUseProtocol);
            Snapshot(window, "provider-debug-over-editor");

            form.UseProtocolCommand.Execute(null);
            await testing.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatch();

            Assert.Equal("openai-chat", editor.Protocol);
            Assert.Empty(window.GetVisualDescendants().OfType<ProviderDebugDialog>());
            Assert.False(editing.IsCompleted); // 编辑弹窗照常开着
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            await editing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_narrow_window_shrinks_the_debugger()
    {
        var (window, dialogs) = Host(dark: false, width: 760, height: 640);
        try
        {
            var shown = dialogs.ShowProviderDebugAsync(Form(new FakeCommands()));
            Dispatch();
            var surface = window.GetVisualDescendants().OfType<ProviderDebugDialog>().Single().GetVisualAncestors().OfType<DialogSurface>().First();
            Assert.True(surface.Bounds.Width <= 760 - 32 + 0.5, surface.Bounds.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Snapshot(window, "provider-debug-narrow");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            await shown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Media_provider_debugger_hides_chat_only_controls_and_displays_volcengine_endpoint()
    {
        var (window, dialogs) = Host(dark: true);
        try
        {
            var commands = new FakeCommands();
            var form = new ProviderDebugViewModel(
                commands,
                "dev",
                new ProviderDebugTargetDto("volcengine-image", Kind: "image"),
                "火山方舟",
                "https://ark.cn-beijing.volces.com/api/v3",
                "volcengine-images",
                knownModels: ["doubao-seedream-4-5-251128"],
                kind: "image");
            var shown = dialogs.ShowProviderDebugAsync(form);
            Dispatch();

            Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/images/generations", Named<SelectableTextBlock>(window, "UrlText").Text);
            Assert.False(Named<NcwSelect>(window, "ThinkingSelect").IsEffectivelyVisible);
            Assert.False(Named<NcwNumberField>(window, "MaxOutputField").IsEffectivelyVisible);
            Assert.False(Named<NcwCheckbox>(window, "ToolCheck").IsEffectivelyVisible);

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            await shown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            window.Close();
        }
    }
}
