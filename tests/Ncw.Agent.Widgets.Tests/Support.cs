using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;
using Ncw.Agent.Widgets;
using Ncw.Agent.Widgets.Cards;

namespace Ncw.Agent.Widgets.Tests;

internal sealed class SeqIds : IIdGenerator
{
    private int _n;

    public string NewId() => $"i{Interlocked.Increment(ref _n):D4}";
}

/// <summary>工具测试的装配:本机环境(不碰文件)、交互闸门、权限登记、可变的转录。</summary>
internal sealed class Rig
{
    public Rig()
    {
        Runtime = new ToolRuntime { Environment = new LocalEnvironment("dev-1") };
        Gate = new InteractionGate(new SeqIds());
        Runs = new PermissionRuns();
        Tools = VisualizeTools.Create(Runs, new FixedWidgetTranscript(_ => History));
    }

    public ToolRuntime Runtime { get; }

    public InteractionGate Gate { get; }

    public PermissionRuns Runs { get; }

    public IReadOnlyList<IAgentTool> Tools { get; }

    public List<Message> History { get; } = [];

    public List<ToolProgress> Progress { get; } = [];

    public IAgentTool Tool(string id) => Tools.Single(t => t.Info.InternalId == id);

    public PermissionRunState Register(RunId runId, int depth = 0, string session = "s1")
    {
        var state = PermissionRunState.FromRuntime(Runtime, runId, session, Gate.OpenRun(runId, depth), PermissionMode.Full);
        Runs.Register(state);
        return state;
    }

    public ExecutionToolContext Ctx(RunId runId, string callId = "call-1", int depth = 0, string session = "s1") =>
        new(new ToolContext(runId, callId, session, depth, new EnvironmentTag("dev-1", "local"), new Sink(Progress), new FileMutationQueue()), Runtime);

    public async Task<ToolResult> Run(string toolId, string json, ExecutionToolContext ctx)
    {
        var tool = Tool(toolId);
        var check = tool.PrepareArguments(Jsn.Parse(json));
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!);
        }

        return await tool.ExecuteAsync(new ToolInvocation(ctx.CallId, toolId, tool, check.Input), ctx, default);
    }

    public static string Text(ToolResult r) => ((TextPart)r.Content[0]).Text;

    /// <summary>把一次 show / edit 的结果作为已提交的 tool_result 放进转录(edit / update / runAction 靠它找卡)。</summary>
    public void Commit(string callId, ToolResult result) =>
        History.Add(new Message(new MessageId($"m{History.Count}"), Role.User, [new ToolResultPart(callId, new ToolOutput(Text(result), Details: result.Details), result.IsError)], DateTimeOffset.UnixEpoch));

    private sealed class Sink(List<ToolProgress> into) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value) => into.Add(value);
    }
}

internal static class Json
{
    /// <summary>字符串的 JSON 字面量(测试里拼入参用)。</summary>
    public static string Esc(string raw) => System.Text.Json.Nodes.JsonValue.Create(raw)!.ToJsonString();
}
