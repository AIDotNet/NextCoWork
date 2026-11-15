using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>
/// SSH 的两种交互(06 §1.4、03 §4):<c>SshHostKey</c>(首次出现的主机指纹)与 <c>SshCredential</c>(密码 / 口令 / 键盘交互式)。
/// 走的是同一个交互闸门:先到先得、带执行环境标识(设备 + 环境)、取消清理;回答里的秘密不进任何事件。
/// </summary>
public sealed class SshInteractionTests
{
    private static readonly ExecutionEnvironmentRef Env = new("dev-1", "build-box", "build-box", "ssh");

    private readonly InteractionGate _gate = new(new SequentialIds());
    private readonly List<(RunId Run, AgentEvent Event)> _events = [];

    public SshInteractionTests() =>
        _gate.RunEvent += (r, e) =>
        {
            lock (_events)
            {
                _events.Add((r, e));
            }
        };

    private static SshHostKey HostKey() => new(Env, "[10.0.0.5]:2222", "ssh-ed25519", "abcDEF123");

    private static SshCredential Password(bool canRemember = true) => new(Env, SshCredentialPromptKind.Password, "10.0.0.5", "dev", CanRemember: canRemember);

    private static SshCredential KeyboardInteractive() => new(
        Env, SshCredentialPromptKind.KeyboardInteractive, "10.0.0.5", "dev", Instruction: "two-step",
        Prompts: [new SshCredentialPrompt("Password: ", false), new SshCredentialPrompt("Code: ", true)]);

    private RunInteractions OpenScope(string id = "env:build-box:1") => _gate.OpenRun(new RunId(id));

    [Fact]
    public async Task A_host_key_confirmation_is_answered_once_by_whoever_is_first()
    {
        var scope = OpenScope();
        var task = scope.RequestAsync(HostKey());
        var pending = Assert.IsType<SshHostKey>(Assert.Single(_gate.Snapshot().Pending));
        Assert.Equal(("dev-1", "build-box", "ssh"), (pending.Env.DeviceId, pending.Env.Environment, pending.Env.Type));
        Assert.Equal("ssh_host_key", pending.Kind);

        var first = _gate.Respond(pending.Id, pending.Version, new SshHostKeyResponse(true), "phone");
        Assert.IsType<RespondResult.Accepted>(first);
        // 即使答案一模一样,第二个控制端也得到 conflict(带谁、什么时候)
        var second = Assert.IsType<RespondResult.Conflict>(_gate.Respond(pending.Id, pending.Version, new SshHostKeyResponse(true), "desktop"));
        Assert.Equal(ConflictReason.AlreadyResolved, second.Reason);
        Assert.Equal("phone", second.Resolution!.ResolvedBy);

        Assert.True(((SshHostKeyResponse)await task).Trust);
        scope.End(RunStatus.Done);
    }

    [Fact]
    public async Task A_host_key_confirmation_needs_the_execution_environment()
    {
        var scope = OpenScope();
        await Assert.ThrowsAsync<ArgumentException>(() => scope.RequestAsync(HostKey() with { Env = null! }));
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public async Task A_response_of_the_wrong_kind_does_not_consume_the_interaction()
    {
        var scope = OpenScope();
        var task = scope.RequestAsync(HostKey());
        var pending = Assert.Single(_gate.Snapshot().Pending);
        var invalid = Assert.IsType<RespondResult.Invalid>(_gate.Respond(pending.Id, pending.Version, new SshCredentialResponse(["x"]), "ctrl"));
        Assert.Contains("kind", invalid.Reason);
        Assert.Single(_gate.Snapshot().Pending);

        _gate.Respond(pending.Id, pending.Version, new SshHostKeyResponse(false), "ctrl");
        Assert.False(((SshHostKeyResponse)await task).Trust);
    }

    [Theory]
    [InlineData(false, new[] { "pw" }, false, true)]
    [InlineData(false, new[] { "pw" }, true, true)]
    [InlineData(false, new[] { "" }, false, false)] // 空密码一定被拒:不消耗交互,让用户重填
    [InlineData(false, new[] { "a", "b" }, false, false)] // 数量不对
    [InlineData(true, new[] { "pw", "123456" }, false, true)]
    [InlineData(true, new[] { "pw", "" }, false, true)] // 键盘交互式的个别回答可以为空
    [InlineData(true, new[] { "pw" }, false, false)]
    public void Credential_answers_are_validated(bool keyboardInteractive, string[] answers, bool remember, bool valid)
    {
        var request = keyboardInteractive ? KeyboardInteractive() : Password();
        Assert.Equal(valid, request.Validate(new SshCredentialResponse(answers, remember)) is null);
    }

    [Fact]
    public void Cancelling_is_a_valid_answer_and_remembering_needs_permission()
    {
        Assert.Null(Password().Validate(new SshCredentialResponse(null)));
        Assert.NotNull(Password(canRemember: false).Validate(new SshCredentialResponse(["pw"], Remember: true)));
        Assert.Null(Password(canRemember: false).Validate(new SshCredentialResponse(["pw"])));
        Assert.NotNull(Password().Validate(new SshCredentialResponse([new string('x', SshCredential.MaxAnswerLength + 1)])));
        Assert.NotNull(Password().Validate(new SshCredentialResponse(["a\0b"])));
    }

    [Fact]
    public async Task A_keyboard_interactive_prompt_needs_questions()
    {
        var scope = OpenScope();
        await Assert.ThrowsAsync<ArgumentException>(() => scope.RequestAsync(KeyboardInteractive() with { Prompts = [] }));
    }

    [Fact]
    public async Task The_secret_answer_reaches_the_waiter_but_never_the_resolved_event()
    {
        var scope = OpenScope();
        var task = scope.RequestAsync(Password());
        var pending = Assert.Single(_gate.Snapshot().Pending);
        Assert.True(pending.RedactsResponse);
        Assert.IsType<RespondResult.Accepted>(_gate.Respond(pending.Id, pending.Version, new SshCredentialResponse(["s3cret-pw"], Remember: true), "ctrl"));

        var answer = Assert.IsType<SshCredentialResponse>(await task);
        Assert.Equal(["s3cret-pw"], answer.Answers);
        Assert.True(answer.Remember);

        var resolved = Assert.Single(_events.Select(e => e.Event).OfType<InteractionResolvedEvent>());
        Assert.Null(resolved.Response); // 回答不进事件流
        Assert.Equal(ResolutionStatus.Answered, resolved.Resolution.Status);
        // 请求事件里只有「问什么」
        var requested = Assert.Single(_events.Select(e => e.Event).OfType<InteractionRequestEvent>());
        Assert.DoesNotContain("s3cret-pw", requested.Interaction.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-pw", answer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-pw", resolved.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_interactions_still_put_their_response_in_the_event()
    {
        var scope = OpenScope();
        var task = scope.RequestAsync(HostKey());
        var pending = Assert.Single(_gate.Snapshot().Pending);
        Assert.False(pending.RedactsResponse);
        _gate.Respond(pending.Id, pending.Version, new SshHostKeyResponse(true), "ctrl");
        await task;
        Assert.NotNull(_events.Select(e => e.Event).OfType<InteractionResolvedEvent>().Single().Response);
    }

    [Fact]
    public async Task Cancellation_clears_the_prompt_as_aborted()
    {
        var scope = OpenScope();
        using var cts = new CancellationTokenSource();
        var task = scope.RequestAsync(Password(), cts.Token);
        var pending = Assert.Single(_gate.Snapshot().Pending);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(_gate.Snapshot().Pending);
        var resolution = _gate.GetResolution(pending.Id)!;
        Assert.Equal(ResolutionStatus.Aborted, resolution.Status);
        // 之后才到的回答得到 conflict(已中止),不是 not_found
        Assert.IsType<RespondResult.Conflict>(_gate.Respond(pending.Id, pending.Version, new SshCredentialResponse(["pw"]), "late"));
    }

    [Fact]
    public async Task Ending_the_scope_aborts_what_it_still_has_pending()
    {
        var scope = OpenScope();
        var task = scope.RequestAsync(HostKey());
        scope.End(RunStatus.Done);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public void The_credential_response_does_not_print_its_secret()
    {
        var text = new SshCredentialResponse(["hunter2", "another"], true).ToString();
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("another", text, StringComparison.Ordinal);
        Assert.Contains("2 redacted", text, StringComparison.Ordinal);
    }
}
