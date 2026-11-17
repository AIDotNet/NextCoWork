using System.Runtime.CompilerServices;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 会话标题的自动生成(移植自 2.x 的 <c>session-title.test.ts</c>):纯函数 + 经协议的端到端。
/// 端到端里标题请求由 <see cref="TitleLlm"/> 单独接住,不占用正文的脚本化响应。
/// </summary>
public sealed class SessionTitleTests
{
    // ───────────────────────── 纯函数 ─────────────────────────

    [Theory]
    [InlineData("“修复登录权限问题”", "修复登录权限问题")]
    [InlineData("标题:修复登录权限问题", "修复登录权限问题")]
    [InlineData("Title: Fix the login bug", "Fix the login bug")]
    [InlineData("```text\nFix the login bug\n```", "Fix the login bug")]
    [InlineData("  `Fix the login bug`  ", "Fix the login bug")]
    public void A_model_answer_is_cleaned_down_to_the_bare_title(string answer, string expected) =>
        Assert.Equal(expected, SessionTitleGenerator.TitleOf(answer));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sure!\nHere is a title")]
    [InlineData("<b>bold</b>")]
    [InlineData("新对话")]
    [InlineData("New chat")]
    [InlineData("“新对话”")]
    public void An_answer_that_is_not_a_title_is_rejected_so_the_preview_stays(string answer) =>
        Assert.Null(SessionTitleGenerator.TitleOf(answer));

    [Fact]
    public void The_preview_folds_whitespace_and_caps_at_80_code_points_without_splitting_a_surrogate_pair()
    {
        Assert.Equal("a b c", SessionTitleGenerator.PreviewOf("  a \n\n b\t c  "));
        var emoji = string.Concat(Enumerable.Repeat("😀", 100));
        var preview = SessionTitleGenerator.PreviewOf(emoji);
        Assert.Equal(80, preview.EnumerateRunes().Count());
        Assert.Equal(160, preview.Length); // 每个 emoji 两个 UTF-16 单元:没有被劈开
    }

    [Fact]
    public void A_message_without_text_has_no_title_text_and_input_is_capped()
    {
        var empty = new Message(new MessageId("m"), Ncw.Agent.Llm.Role.User, [new ImagePart("image/png", "ncw://x")], DateTimeOffset.UnixEpoch);
        Assert.Equal("", SessionTitleGenerator.TextOf(empty));
        var long_ = new Message(new MessageId("m"), Ncw.Agent.Llm.Role.User, [new TextPart(new string('x', 5_000))], DateTimeOffset.UnixEpoch);
        Assert.Equal(SessionTitleGenerator.InputLimit, SessionTitleGenerator.TextOf(long_).Length);
    }

    // ───────────────────────── 端到端 ─────────────────────────

    private const string FirstMessage = "帮我检查登录页面的权限问题,并补上必要的修复。";

    [Fact]
    public async Task The_first_message_gets_an_immediate_preview_then_the_generated_title_replaces_it()
    {
        await using var t = await TitleRig.StartAsync();
        t.Title.Answer("“修复登录权限问题”");
        var session = await t.NewSessionAsync();

        await t.RunAsync(session.Id, FirstMessage);

        // 预览是同步写的:此刻模型的回答还被闸门拦着,标题已经不是「新对话」了
        var preview = await t.WaitSessionAsync(session.Id, s => s.TitleSource == "generated", "preview");
        Assert.Equal(FirstMessage, preview.Title);
        await t.Title.WaitForRequestAsync();

        t.Title.Release();
        var done = await t.WaitSessionAsync(session.Id, s => s.Title == "修复登录权限问题", "generated title");
        Assert.Equal("generated", done.TitleSource);
        Assert.Equal(preview.UpdatedAt, done.UpdatedAt); // 自动起名不把会话顶到最前面

        // 旁路请求:无工具、只有第一条消息、独立 RunId、按会话记账、思考尽量关闭
        var (request, context) = Assert.Single(t.Title.Requests);
        Assert.Equal(SessionTitleGenerator.Prompt, request.System);
        Assert.Empty(request.Tools);
        var message = Assert.Single(request.Messages);
        Assert.Equal(FirstMessage, Assert.IsType<TextPart>(Assert.Single(message.Parts)).Text);
        Assert.StartsWith("title_", context.RunId);
        Assert.Equal(session.Id, context.SessionId);
        Assert.Equal(ThinkingLevel.Off, request.Options.Thinking);
        Assert.True(request.Options.MaxOutputTokens <= 256);
    }

    [Fact]
    public async Task Later_messages_do_not_ask_for_another_title()
    {
        await using var t = await TitleRig.StartAsync();
        t.Title.Answer("第一个标题");
        t.Title.Release();
        var session = await t.NewSessionAsync();
        await t.RunAsync(session.Id, "first question");
        await t.WaitSessionAsync(session.Id, s => s.Title == "第一个标题", "title");

        await t.RunAsync(session.Id, "second question");
        await Task.Delay(100);

        Assert.Single(t.Title.Requests);
        Assert.Equal("第一个标题", (await t.GetAsync(session.Id)).Title);
    }

    [Fact]
    public async Task A_conversation_the_user_already_named_is_left_alone()
    {
        await using var t = await TitleRig.StartAsync();
        var session = await t.NewSessionAsync();
        await t.Client.CallAsync(ConversationMethods.SessionsRename, new SessionsRenameParams(session.Id, "My project"));

        await t.RunAsync(session.Id, FirstMessage);
        await Task.Delay(100);

        Assert.Empty(t.Title.Requests);
        var after = await t.GetAsync(session.Id);
        Assert.Equal(("My project", "manual"), (after.Title, after.TitleSource));
    }

    [Theory]
    [InlineData("My manual name")]
    [InlineData("新对话")] // 手动改回占位名也是用户的选择
    public async Task A_manual_rename_made_while_the_request_is_pending_wins(string name)
    {
        await using var t = await TitleRig.StartAsync();
        t.Title.Answer("模型起的标题");
        var session = await t.NewSessionAsync();
        await t.RunAsync(session.Id, FirstMessage);
        await t.WaitSessionAsync(session.Id, s => s.TitleSource == "generated", "preview");
        await t.Title.WaitForRequestAsync();

        await t.Client.CallAsync(ConversationMethods.SessionsRename, new SessionsRenameParams(session.Id, name));
        t.Title.Release();
        await t.Title.WaitForFinishedAsync();
        await Task.Delay(100);

        var after = await t.GetAsync(session.Id);
        Assert.Equal((name, "manual"), (after.Title, after.TitleSource));
    }

    [Fact]
    public async Task A_deleted_conversation_is_not_resurrected_by_a_late_title()
    {
        await using var t = await TitleRig.StartAsync();
        t.Title.Answer("模型起的标题");
        var session = await t.NewSessionAsync();
        await t.RunAsync(session.Id, FirstMessage);
        await t.WaitSessionAsync(session.Id, s => s.TitleSource == "generated", "preview");
        await t.Title.WaitForRequestAsync();

        await t.Client.CallAsync(ConversationMethods.SessionsDelete, new SessionRefParams(session.Id));
        t.Title.Release();
        await t.Title.WaitForFinishedAsync();
        await Task.Delay(100);

        var gone = await Assert.ThrowsAsync<Ncw.Protocol.Wire.ProtocolException>(() => t.GetAsync(session.Id));
        Assert.Equal(Ncw.Protocol.Wire.ErrorCodes.NotFound, gone.Code);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("truncated")]
    [InlineData("multiline")]
    [InlineData("tool")]
    [InlineData("reasoning-only")]
    public async Task A_failed_or_unusable_answer_keeps_the_preview(string failure)
    {
        await using var t = await TitleRig.StartAsync();
        t.Title.Script = failure switch
        {
            "error" => [new StreamError(AgentError.Create(AgentErrorCode.Provider, "Unavailable", false))],
            "truncated" => [new TextDelta(0, "半截标题"), new MessageEnd(StopReason.MaxTokens, new TokenUsage(10, 5))],
            "multiline" => [new TextDelta(0, "Sure!\nHere is a title"), new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5))],
            "tool" => [new ToolCallStart(0, "c1", "Write"), new MessageEnd(StopReason.ToolUse, new TokenUsage(10, 5))],
            _ => [new ThinkingDelta(0, "Do not use this"), new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5))],
        };
        t.Title.Release();
        var session = await t.NewSessionAsync();

        await t.RunAsync(session.Id, FirstMessage);
        await t.Title.WaitForFinishedAsync();
        await Task.Delay(100);

        var after = await t.GetAsync(session.Id);
        Assert.Equal((FirstMessage, "generated"), (after.Title, after.TitleSource));
    }

    // ───────────────────────── 台架 ─────────────────────────

    /// <summary>分流:标题请求(按系统提示词认)进 <see cref="Title"/>,其余原样交给脚本化的正文上游。</summary>
    private sealed class TitleLlm(FakeLlm body) : ILlmClient
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(LlmRequest Request, RequestContext Context)> Requests { get; } = [];

        public IReadOnlyList<LlmEvent> Script { get; set; } = [];

        public void Answer(string title) =>
            Script = [new TextDelta(0, title), new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5))];

        /// <summary>放行被拦住的标题回答。</summary>
        public void Release() => _gate.TrySetResult();

        public Task WaitForRequestAsync() => _arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task WaitForFinishedAsync() => _finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (request.System != SessionTitleGenerator.Prompt)
            {
                await foreach (var ev in body.StreamAsync(model, request, context, ct))
                {
                    yield return ev;
                }

                yield break;
            }

            lock (Requests)
            {
                Requests.Add((request, context));
            }

            _arrived.TrySetResult();
            try
            {
                await _gate.Task.WaitAsync(ct);
                foreach (var ev in Script)
                {
                    yield return ev;
                }
            }
            finally
            {
                _finished.TrySetResult();
            }
        }
    }

    private sealed class TitleRig : IAsyncDisposable
    {
        private readonly ConversationRig _rig;

        private TitleRig(ConversationRig rig, DeviceClient client, ProjectDto project, TitleLlm title)
        {
            _rig = rig;
            Client = client;
            Project = project;
            Title = title;
        }

        public DeviceClient Client { get; }

        public ProjectDto Project { get; }

        public TitleLlm Title { get; }

        public static async Task<TitleRig> StartAsync()
        {
            TitleLlm? title = null;
            var rig = new ConversationRig(llm =>
            {
                title = new TitleLlm(Assert.IsType<FakeLlm>(llm));
                // 默认台架的装配,只换上游并显式打开标题(注入假模型解析时标题默认关)
                return new AgentServices
                {
                    Models = new FakeModels(title),
                    Http = new NoNetwork(),
                    CatalogDelayer = new NeverDelay(),
                    Delayer = new NoDelay(),
                    Loop = new AgentLoopOptions { ResumeDelays = [] },
                    SessionTitles = true,
                };
            });
            await rig.InitializeAsync();
            var client = await rig.ConnectAsync();
            var project = await rig.AddProjectAsync(client);
            return new TitleRig(rig, client, project, title!);
        }

        public Task<SessionDto> NewSessionAsync() => _rig.CreateSessionAsync(Client, Project);

        /// <summary>发一条消息并等这个 Run 收尾(正文上游每次都答「ok」)。</summary>
        public async Task RunAsync(string sessionId, string text)
        {
            _rig.Llm.Then(Up.Say("ok"));
            var start = await _rig.StartAsync(Client, sessionId, text);
            await (await RunWatch.OpenAsync(Client, start.RunId)).SettledAsync();
        }

        public async Task<SessionDto> GetAsync(string sessionId) =>
            (await Client.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(sessionId))).Session;

        public async Task<SessionDto> WaitSessionAsync(string sessionId, Func<SessionDto, bool> predicate, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var session = await GetAsync(sessionId);
                if (predicate(session))
                {
                    return session;
                }

                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what} (title: {session.Title}, source: {session.TitleSource}).");
                await Task.Delay(20);
            }
        }

        public ValueTask DisposeAsync() => _rig.DisposeAsync();
    }
}
