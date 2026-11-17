using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>审批卡片的「要做什么」预览与一步完成的「总是允许 ▾」(docs/design/03 §11.3)。</summary>
public class ApprovalPreviewTests
{
    private sealed class Commands : IConversationCommands
    {
        public List<InteractionResponseDto> Responses { get; } = [];

        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(new SendResult(SendOutcome.Started));

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SetModelAsync(SessionRef session, string alias, string? providerId, string? defaultForWorkspace = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default)
        {
            Responses.Add(response);
            return Task.FromResult(new InteractionResult(InteractionOutcome.Answered));
        }

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }

    private static ToolPermissionInteractionDto Tool(string name, string input, params string[] scopes) => new()
    {
        Id = "i1", RunId = "run-1", CallId = "c1", ToolName = name, InternalId = name.ToLowerInvariant(), InputJson = input,
        Env = new EnvRefDto("local", "local", "local", "local"), AllowedScopes = scopes, Risk = "unknown", CreatedAt = 0, Version = 1,
        SuggestedRule = name + "(*)",
    };

    private static InteractionRowViewModel Card(ToolPermissionInteractionDto tool, Commands? commands = null) =>
        new(new InteractionRow("i:i1", tool), "local", commands ?? new Commands(), id => id, _ => { }, TimeProvider.System);

    [Fact]
    public void Write_shows_the_content_as_added_lines_with_stats()
    {
        var card = Card(Tool("Write", "{\"file_path\":\"/srv/main.go\",\"content\":\"package main\\n\\nfunc main() {}\\n\"}"));

        Assert.Equal("/srv/main.go", card.Target);
        Assert.True(card.HasPreview);
        Assert.False(card.IsCommandPreview);
        Assert.All(card.PreviewLines, l => Assert.True(l.IsAdded));
        Assert.Equal("package main", card.PreviewLines[0].Text);
        Assert.Equal("+3", card.PreviewAddedText);
    }

    [Fact]
    public void Edit_shows_removed_then_added_and_counts_what_does_not_fit()
    {
        var lines = string.Join("\\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var card = Card(Tool("Edit", $"{{\"file_path\":\"a.cs\",\"old_string\":\"old\",\"new_string\":\"{lines}\"}}"));

        Assert.True(card.PreviewLines[0].IsRemoved);
        Assert.True(card.PreviewLines[1].IsAdded);
        Assert.Equal(TranscriptBuilder.ApprovalPreviewLineLimit, card.PreviewLines.Count);
        Assert.Equal(21 - TranscriptBuilder.ApprovalPreviewLineLimit, card.PreviewMore);
        Assert.True(card.HasPreviewMore);
    }

    [Fact]
    public void Multi_line_commands_show_in_full_and_one_liners_are_not_repeated()
    {
        var multi = Card(Tool("Bash", "{\"command\":\"cd build &&\\n  make -j8 &&\\n  ./run-tests\"}"));
        Assert.True(multi.IsCommandPreview);
        Assert.Equal(["cd build &&", "  make -j8 &&", "  ./run-tests"], multi.PreviewLines.Select(l => l.Text));

        var single = Card(Tool("Bash", "{\"command\":\"ls -la\"}"));
        Assert.Equal("ls -la", single.Target);
        Assert.False(single.HasPreview);
    }

    [Fact]
    public async Task Always_allow_in_a_scope_answers_in_one_step()
    {
        var commands = new Commands();
        var card = Card(Tool("Bash", "{\"command\":\"npm test\"}", "session", "project"), commands);

        await card.AlwaysAllowInCommand.ExecuteAsync("project");
        var response = Assert.IsType<ToolPermissionResponseDto>(Assert.Single(commands.Responses));
        Assert.Equal(PermissionChoices.AllowAlways, response.Choice);
        Assert.Equal("project", response.Scope);

        await card.AlwaysAllowInCommand.ExecuteAsync("global"); // 设备没给的范围:不回答
        Assert.Single(commands.Responses);
    }
}
