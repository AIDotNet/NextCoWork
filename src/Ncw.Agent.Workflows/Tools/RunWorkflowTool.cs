using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Tools.Schema;
using Ncw.Agent.Workflows.Engine;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workflows.Tools;

/// <summary>供主 Agent 调用的工作流启动抽象(由 Host 注入,隔离协议与存储)。</summary>
public interface IWorkflowToolLauncher
{
    Task<WorkflowRunRecord> LaunchFromToolAsync(
        string parentRunId,
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, string> inputs,
        bool runInBackground,
        Action<WorkflowRunRecord>? onStarted,
        CancellationToken ct);
}

/// <summary>
/// <c>RunWorkflow</c> 内置工具(Claude Code 极简风格动态子代理 DAG 编排):
/// 主 Agent 只需声明 <c>title</c> 与若干 <c>steps</c>(每个步骤是一个独立子代理任务,含 <c>id</c> / <c>title</c> / <c>prompt</c> / 可选 <c>depends_on</c> / 可选 <c>model</c>),
/// 运行时自动并行/串行调度、自动把上游依赖结果注入下游提示词,并将实时节点进度流式推送到对话面板的 Workflow 画布。
/// </summary>
public sealed class RunWorkflowTool(IWorkflowToolLauncher? launcher) : IAgentTool
{
    public const string Name = "RunWorkflow";

    private static readonly JsonObject StepSchema = S.Object(
        ["id", "title", "prompt"],
        ("id", S.String("Short unique step id (e.g. 'inspect_api', 'write_tests', 'summarize')", minLength: 1, maxLength: 64)),
        ("title", S.String("Short step title shown on the workflow canvas node", minLength: 1, maxLength: 120)),
        ("prompt", S.String("Clear task instruction for this subagent. Results from depends_on steps are automatically provided as context (you can also reference {{nodes.<id>.output}} explicitly).", minLength: 1)),
        ("depends_on", S.Array("Optional upstream step ids that must finish before this step runs. Steps without shared dependencies run concurrently.", S.String("Upstream step id"))),
        ("model", S.String("Optional model alias override; omit to follow the current session model.")));

    private static readonly JsonElement Schema = S.Build(S.Object(
        ["title", "steps"],
        ("title", S.String("Short workflow title displayed in the conversation card", minLength: 1, maxLength: 120)),
        ("steps", S.Array("List of subagent steps forming a DAG via depends_on", StepSchema)),
        ("run_in_background", S.Boolean("If true, start the workflow and return its runId immediately without waiting"))));

    public ToolSpec Info { get; } = new(
        Name,
        "Run a parallel/sequential DAG of subagent tasks for complex work. Each step runs in an isolated subagent session with full workspace tool access and inherits the current session model. Independent steps run concurrently; steps with depends_on wait for upstream steps and automatically receive their outputs. Live progress renders in the conversation workflow canvas.",
        Schema,
        new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: false));

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        if (launcher is null)
        {
            return ToolResult.Error("Workflow engine is not available in this runtime.");
        }

        var arguments = call.Input;
        var title = arguments.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return ToolResult.Error("Workflow 'title' is required.");
        }

        if (!arguments.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array || stepsEl.GetArrayLength() == 0)
        {
            return ToolResult.Error("Workflow 'steps' must be a non-empty array.");
        }

        var background = arguments.TryGetProperty("run_in_background", out var bgEl) && bgEl.ValueKind == JsonValueKind.True;

        var (definition, parseError) = BuildDefinition(title.Trim(), stepsEl);
        if (parseError is not null || definition is null)
        {
            return ToolResult.Error(parseError ?? "Invalid workflow steps.");
        }

        var errors = WorkflowValidator.Validate(definition);
        if (errors.Count > 0)
        {
            return ToolResult.Error($"Invalid workflow DAG: {string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"))}");
        }

        try
        {
            var run = await launcher.LaunchFromToolAsync(
                ctx.RunId.Value,
                definition,
                new Dictionary<string, string>(StringComparer.Ordinal),
                background,
                onStarted: started =>
                {
                    var progressJson = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["type"] = "workflow_run",
                        ["runId"] = started.RunId,
                        ["title"] = started.Title,
                        ["status"] = WorkflowEngine.FormatRunStatus(started.Status),
                    }, WorkflowJsonContext.Default.DictionaryStringString);
                    using var progressDoc = JsonDocument.Parse(progressJson);
                    ctx.Progress.Report(new ToolProgress($"Running workflow '{started.Title}'", Details: progressDoc.RootElement.Clone()));
                },
                ct).ConfigureAwait(false);
            var detailsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["type"] = "workflow_run",
                ["runId"] = run.RunId,
                ["title"] = run.Title,
                ["status"] = WorkflowEngine.FormatRunStatus(run.Status),
            }, WorkflowJsonContext.Default.DictionaryStringString);
            using var detailsDoc = JsonDocument.Parse(detailsJson);
            var details = detailsDoc.RootElement.Clone();

            if (background)
            {
                return ToolResult.Text($"Workflow '{run.Title}' started in background (runId: {run.RunId}).", details);
            }

            if (run.Status == WorkflowRunStatus.Completed)
            {
                return ToolResult.Text(run.OutputText ?? $"Workflow '{run.Title}' completed (runId: {run.RunId}).", details);
            }

            return ToolResult.Error($"Workflow '{run.Title}' ended with status {run.Status}: {run.Error ?? "no details"} (runId: {run.RunId}).", details);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    internal static (WorkflowDefinition? Definition, string? Error) BuildDefinition(string title, JsonElement stepsEl)
    {
        var nodes = new List<WorkflowNodeDef>();
        var edges = new List<WorkflowEdgeDef>();
        var edgeIndex = 1;

        foreach (var step in stepsEl.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                return (null, "Each item in 'steps' must be an object.");
            }

            var id = GetString(step, "id");
            var stepTitle = GetString(step, "title") ?? GetString(step, "label");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(stepTitle))
            {
                return (null, "Each step requires non-empty 'id' and 'title'.");
            }

            var prompt = GetString(step, "prompt") ?? GetString(step, "description");
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return (null, $"Step '{id}' requires a non-empty 'prompt'.");
            }

            var config = new WorkflowNodeConfig(
                Prompt: prompt,
                Model: GetString(step, "model"));

            nodes.Add(new WorkflowNodeDef(
                Id: id,
                Kind: WorkflowNodeKind.Agent,
                Title: stepTitle,
                Config: config));

            if (step.TryGetProperty("depends_on", out var depsEl) && depsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in depsEl.EnumerateArray())
                {
                    if (dep.ValueKind == JsonValueKind.String && dep.GetString() is { Length: > 0 } srcId)
                    {
                        edges.Add(new WorkflowEdgeDef($"e{edgeIndex++}", srcId, id));
                    }
                }
            }
        }

        var wfId = $"wf-{Guid.NewGuid():N}"[..16];
        return (new WorkflowDefinition(wfId, title, nodes, edges), null);
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
