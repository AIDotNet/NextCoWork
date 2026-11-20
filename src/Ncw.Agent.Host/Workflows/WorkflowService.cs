using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Services;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Agent.Tools;
using Ncw.Agent.Workflows.Engine;
using Ncw.Agent.Workflows.Tools;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Workflows;

/// <summary>
/// Host 侧的对话工作流服务:将 <see cref="RunWorkflowTool"/> 注册为主 Agent 内置工具,
/// 经 <see cref="WorkflowEngine"/> 并行/串行调度独立子会话 Agent 与工具节点,并将实时状态推到 <c>workflow/&lt;id&gt;</c> 主题。
/// </summary>
internal sealed class WorkflowService : IWorkflowNodeExecutor, IWorkflowToolLauncher
{
    private readonly ConversationService _conversation;
    private readonly TopicHub _topics;
    private readonly TimeProvider _clock;
    private readonly WorkflowRunRepository _repository;
    private readonly WorkflowEngine _engine;

    public WorkflowService(AgentDatabase database, ConversationService conversation, TopicHub topics, TimeProvider clock)
    {
        _conversation = conversation;
        _topics = topics;
        _clock = clock;
        _repository = new WorkflowRunRepository(database);
        _repository.FailInterruptedOnStartup(clock.GetUtcNow().ToUnixTimeMilliseconds());

        _engine = new WorkflowEngine(_repository, this, clock);
        _engine.Updated += OnEngineUpdated;

        // 将 RunWorkflow 注册为主 Agent 内置工具
        _conversation.ToolRegistry.Register(new RunWorkflowTool(this), ToolRegistry.BuiltinSource);
    }

    private void OnEngineUpdated(WorkflowEngineEvent evt)
    {
        var dto = WorkflowMapping.ToEvent(evt);
        _topics.Publish(WorkflowMethods.RunTopic(evt.RunId), dto.Kind, dto, PayloadShapes.GetShape<WorkflowRunEventDto>());
    }

    public WorkflowRunSnapshotDto? RunSnapshot(string runId) =>
        _engine.GetSnapshot(runId) is { } run ? WorkflowMapping.ToSnapshot(run) : null;

    public void Cancel(string runId) => _engine.Cancel(runId);

    public WorkflowRunSnapshotDto Resume(string runId)
    {
        try
        {
            return WorkflowMapping.ToSnapshot(_engine.Resume(runId));
        }
        catch (KeyNotFoundException ex)
        {
            throw ServiceMapping.Error(ErrorCodes.NotFound, "workflows.runNotFound", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw ServiceMapping.Error(ErrorCodes.Conflict, "workflows.cannotResume", ex.Message);
        }
    }

    public WorkflowRunSnapshotDto RetryNode(string runId, string nodeId)
    {
        try
        {
            return WorkflowMapping.ToSnapshot(_engine.RetryNode(runId, nodeId));
        }
        catch (KeyNotFoundException ex)
        {
            throw ServiceMapping.Error(ErrorCodes.NotFound, "workflows.notFound", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw ServiceMapping.Error(ErrorCodes.Conflict, "workflows.cannotRetry", ex.Message);
        }
    }

    public WorkflowRunsListResult ListRuns(WorkflowRunsListParams p)
    {
        var rows = _engine.ListRuns(p.WorkspaceId, p.SessionId, p.Limit);
        var items = rows.Select(r => WorkflowMapping.ToListItem(r, _repository.GetLatestNodes(r.Id))).ToArray();
        return new WorkflowRunsListResult(items);
    }

    public WorkflowRunSnapshotDto GetRun(string runId) =>
        RunSnapshot(runId) ?? throw ServiceMapping.Error(ErrorCodes.NotFound, "workflows.runNotFound", $"Workflow run '{runId}' not found.");

    // ───────────────────────── IWorkflowToolLauncher ─────────────────────────

    public async Task<WorkflowRunRecord> LaunchFromToolAsync(
        string parentRunId,
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, string> inputs,
        bool runInBackground,
        Action<WorkflowRunRecord>? onStarted,
        CancellationToken ct)
    {
        var parentRun = _conversation.Registry.Get(new RunId(parentRunId))
            ?? throw new InvalidOperationException("Parent run is not active.");
        var session = _conversation.RequireSession(parentRun.SessionId);
        var project = _conversation.RequireProject(session.WorkspaceId);
        var env = await _conversation.EnvironmentOfAsync(project, ct).ConfigureAwait(false);

        var permMode = parentRun.LivePermissionMode is { Length: > 0 } livePerm ? livePerm
            : parentRun.Options?.PermissionMode is { Length: > 0 } optPerm ? optPerm
            : session.PermissionMode is { Length: > 0 } sesPerm ? sesPerm
            : "full";

        var wfRunId = $"wfr_{Guid.NewGuid():N}";
        var started = _engine.Start(new WorkflowLaunchRequest(
            RunId: wfRunId,
            WorkspaceId: session.WorkspaceId,
            Definition: definition,
            Inputs: inputs,
            WorkflowId: definition.Id,
            SessionId: session.Id,
            EnvironmentId: env.DisplayName,
            PermissionMode: permMode,
            NestingDepth: parentRun.Depth + 1));

        onStarted?.Invoke(started);

        return runInBackground
            ? started
            : await _engine.WaitForCompletionAsync(wfRunId, ct).ConfigureAwait(false);
    }

    // ───────────────────────── IWorkflowNodeExecutor ─────────────────────────

    public async Task<WorkflowNodeExecutionResult> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
    {
        var project = _conversation.RequireProject(context.WorkspaceId);
        var env = await _conversation.EnvironmentOfAsync(project, ct).ConfigureAwait(false);

        return context.Node.Kind switch
        {
            WorkflowNodeKind.Tool => await ExecuteToolNodeAsync(context, project, env, ct).ConfigureAwait(false),
            WorkflowNodeKind.Agent => await ExecuteAgentNodeAsync(context, project, ct).ConfigureAwait(false),
            WorkflowNodeKind.Approval => await ExecuteApprovalNodeAsync(context, env, ct).ConfigureAwait(false),
            WorkflowNodeKind.Plugin => new WorkflowNodeExecutionResult(false, Error: "workflows.error.pluginNotImplemented"),
            _ => new WorkflowNodeExecutionResult(true, Summary: "Completed", OutputText: ""),
        };
    }

    private async Task<WorkflowNodeExecutionResult> ExecuteToolNodeAsync(
        WorkflowNodeExecutionContext context,
        WorkspaceRecord project,
        IExecutionEnvironment env,
        CancellationToken ct)
    {
        var toolName = context.Node.Config?.ToolName;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return new WorkflowNodeExecutionResult(false, Error: "workflows.error.missingToolName");
        }

        var tool = _conversation.AllTools().FirstOrDefault(t => string.Equals(t.Info.InternalId, toolName, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
        {
            return new WorkflowNodeExecutionResult(false, Error: $"workflows.error.unknownTool:{toolName}");
        }

        var rawArgs = TemplateResolver.Render(context.Node.Config?.ToolArgsJson ?? "{}", context.Inputs, context.CompletedNodes);
        JsonElement argsElement;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawArgs) ? "{}" : rawArgs);
            argsElement = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new WorkflowNodeExecutionResult(false, Error: $"Invalid tool args JSON: {ex.Message}");
        }

        var runtime = new ToolRuntime
        {
            Environment = env,
            WorkspaceId = project.Id,
            Root = EnvPath.Parse(project.RootPath, env.Facts.PathStyle),
            Shells = _conversation.Shells,
            Clock = _clock,
        };

        var callId = $"{context.RunId}:{context.Node.Id}:a{context.Attempt}";
        var baseCtx = new ToolContext(new RunId(context.RunId), callId, tool.Info.InternalId, context.NestingDepth, null, new Progress<ToolProgress>(), new FileMutationQueue());
        var execCtx = new ExecutionToolContext(baseCtx, runtime);
        var result = await tool.ExecuteAsync(new ToolInvocation(callId, tool.Info.InternalId, tool, argsElement), execCtx, ct).ConfigureAwait(false);
        var text = string.Join("\n", result.Content.OfType<TextPart>().Select(static p => p.Text));
        return new WorkflowNodeExecutionResult(
            Succeeded: !result.IsError,
            Summary: result.IsError ? "Tool failed" : "Tool succeeded",
            OutputText: text,
            Error: result.IsError ? text : null);
    }

    private async Task<WorkflowNodeExecutionResult> ExecuteAgentNodeAsync(
        WorkflowNodeExecutionContext context,
        WorkspaceRecord project,
        CancellationToken ct)
    {
        var rawPrompt = context.Node.Config?.Prompt ?? context.Node.Description ?? context.Node.Title;
        var prompt = TemplateResolver.Render(rawPrompt, context.Inputs, context.CompletedNodes);

        // 若提示词未显式使用 {{nodes. 引用上游结果,自动把直接 depends_on 上游步骤的输出附带为上下文
        if (!rawPrompt.Contains("{{nodes.", StringComparison.Ordinal)
            && context.UpstreamNodeIds is { Count: > 0 } upstreamIds)
        {
            var upstreamBlocks = new List<string>();
            foreach (var upId in upstreamIds)
            {
                if (context.CompletedNodes.TryGetValue(upId, out var upNode)
                    && upNode.Status == WorkflowNodeRunStatus.Done
                    && !string.IsNullOrWhiteSpace(upNode.OutputText))
                {
                    upstreamBlocks.Add($"### {upId}\n{upNode.OutputText}");
                }
            }

            if (upstreamBlocks.Count > 0)
            {
                prompt = $"<upstream_results>\n{string.Join("\n\n", upstreamBlocks)}\n</upstream_results>\n\n{prompt}";
            }
        }
        var childRunId = new RunId($"{context.RunId}:node:{context.Node.Id}:a{context.Attempt}");
        var childSessionId = $"wfs_{context.RunId}_{context.Node.Id}_a{context.Attempt}";

        // 默认跟随父会话的当前模型、供应商、模式与思考档位(优先取正在跑的顶层 Run 档位,重试/续跑时回落到会话持久化的模型配置)
        var parentRunOptions = context.SessionId is { Length: > 0 } liveSid
            ? _conversation.Registry.ActiveTopLevelRun(liveSid)?.Options
            : null;
        var parentSession = context.SessionId is { Length: > 0 } sid
            ? _conversation.Sessions.Get(sid)
            : null;

        var explicitModel = context.Node.Config?.Model;
        ModelRef modelRef;
        if (!string.IsNullOrWhiteSpace(explicitModel))
        {
            modelRef = new ModelRef(null, explicitModel);
        }
        else if (parentRunOptions is { Model.Alias.Length: > 0 })
        {
            modelRef = parentRunOptions.Model;
        }
        else if (parentSession is { Model.Length: > 0 })
        {
            modelRef = new ModelRef(parentSession.ModelProviderId, parentSession.Model);
        }
        else
        {
            modelRef = new ModelRef(null, "");
        }

        var mode = parentRunOptions?.Mode is { Length: > 0 } runMode
            ? runMode
            : parentSession?.Mode is { Length: > 0 } sessionMode ? sessionMode : "code";
        var thinking = parentRunOptions?.Thinking
            ?? (Enum.TryParse<ThinkingLevel>(parentSession?.Thinking, ignoreCase: true, out var parsedThinking)
                ? parsedThinking
                : ThinkingLevel.Auto);
        var maxContext = parentRunOptions?.MaxContext ?? false;

        // 工作流子代理默认完全授权(full),避免并行子任务被工具审批阻塞
        const string subagentPermission = "full";

        _conversation.Sessions.Ensure(new SessionCreate(
            project.Id,
            Id: childSessionId,
            ParentSessionId: context.SessionId,
            Title: context.Node.Title,
            Model: modelRef.Alias,
            ModelProviderId: modelRef.ProviderId,
            Mode: mode,
            Thinking: thinking.ToString().ToLowerInvariant(),
            PermissionMode: subagentPermission,
            RootPathAtCreation: project.RootPath));

        context.ReportProgress(new WorkflowNodeRunRecord(
            NodeId: context.Node.Id,
            Kind: context.Node.Kind,
            Status: WorkflowNodeRunStatus.Running,
            Attempt: context.Attempt,
            ChildRunId: childRunId.Value,
            ChildSessionId: childSessionId));

        var message = new Message(new MessageId($"{childRunId.Value}:prompt"), Ncw.Agent.Llm.Role.User, [new TextPart(prompt)], _clock.GetUtcNow());
        var sendOptions = new SendOptions(modelRef, thinking, maxContext, mode, subagentPermission);
        var runRequest = new RunRequest(childRunId, [message], modelRef, thinking)
        {
            Depth = context.NestingDepth + 1,
        };

        var launched = _conversation.Registry.Launch(new RunLaunch(childSessionId, project.Id, runRequest)
        {
            Origin = RunOrigin.WorkflowNode,
            ParentRunId = null,
            ParentSessionId = context.SessionId,
            Options = sendOptions,
            PermissionMode = subagentPermission,
        });
        if (launched is not LaunchResult.Started started)
        {
            var reason = launched is LaunchResult.Refused r ? r.Detail : "workflows.error.agentLaunchFailed";
            return new WorkflowNodeExecutionResult(false, Error: reason, ChildRunId: childRunId.Value, ChildSessionId: childSessionId);
        }

        using var reg = ct.Register(() => _conversation.Registry.Abort(childRunId, cascade: true));
        if (started.Handle.Completion is { } completion)
        {
            await completion.WaitAsync(ct).ConfigureAwait(false);
        }

        var lastAssistant = _conversation.Sessions.LastVisibleAssistant(childSessionId);
        var outputText = ExtractTextFromPartsJson(lastAssistant?.PartsJson);

        var succeeded = started.Handle.State == RunState.Done;
        return new WorkflowNodeExecutionResult(
            Succeeded: succeeded,
            Summary: succeeded ? "Agent finished" : $"Agent {started.Handle.State}",
            OutputText: outputText,
            Error: succeeded ? null : (started.Handle.Error?.Message ?? started.Handle.State.ToString()),
            ChildRunId: childRunId.Value,
            ChildSessionId: childSessionId);
    }

    private async Task<WorkflowNodeExecutionResult> ExecuteApprovalNodeAsync(
        WorkflowNodeExecutionContext context,
        IExecutionEnvironment env,
        CancellationToken ct)
    {
        var summary = TemplateResolver.Render(context.Node.Config?.SummaryTemplate ?? context.Node.Description ?? context.Node.Title, context.Inputs, context.CompletedNodes);
        context.ReportProgress(new WorkflowNodeRunRecord(
            NodeId: context.Node.Id,
            Kind: context.Node.Kind,
            Status: WorkflowNodeRunStatus.WaitingApproval,
            Attempt: context.Attempt,
            Summary: $"[{env.DisplayName}] {summary}"));

        if (string.Equals(context.PermissionMode, "full", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowNodeExecutionResult(
                Succeeded: true,
                Summary: "Auto-approved (full permission)",
                OutputText: "approved",
                ActiveOutputPorts: ["approved"]);
        }

        var runId = new RunId($"{context.RunId}:approval:{context.Node.Id}:a{context.Attempt}");
        var scope = _conversation.Gate.OpenRun(runId, depth: 0);
        try
        {
            var payloadJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["workflowRunId"] = context.RunId,
                ["nodeId"] = context.Node.Id,
                ["title"] = context.Node.Title,
                ["summary"] = summary,
            }, WorkflowJsonContext.Default.DictionaryStringString);
            using var doc = JsonDocument.Parse(payloadJson);

            var response = await scope.RequestAsync(new ToolPermission
            {
                CallId = $"{context.RunId}:{context.Node.Id}:a{context.Attempt}",
                ToolName = $"Workflow Approval: {context.Node.Title}",
                InternalId = "WorkflowApproval",
                Input = doc.RootElement.Clone(),
                Traits = new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: false),
                Env = ExecutionEnvironmentRef.From(env),
                AllowedScopes = RememberScopes.None,
            }, ct).ConfigureAwait(false);

            if (response is ToolPermissionResponse { Choice: PermissionChoice.DenyChoice deny })
            {
                return new WorkflowNodeExecutionResult(
                    Succeeded: true,
                    Summary: string.IsNullOrWhiteSpace(deny.Reason) ? "Rejected by user" : deny.Reason,
                    OutputText: "rejected",
                    ActiveOutputPorts: ["rejected"]);
            }

            return new WorkflowNodeExecutionResult(
                Succeeded: true,
                Summary: $"Approved on {env.DisplayName}",
                OutputText: "approved",
                ActiveOutputPorts: ["approved"]);
        }
        finally
        {
            scope.End(ct.IsCancellationRequested ? RunStatus.Aborted : RunStatus.Done);
        }
    }

    private static string ExtractTextFromPartsJson(string? partsJson)
    {
        if (string.IsNullOrWhiteSpace(partsJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(partsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var texts = new List<string>();
            foreach (var part in doc.RootElement.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text"
                    && part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    var t = textEl.GetString();
                    if (!string.IsNullOrEmpty(t))
                    {
                        texts.Add(t);
                    }
                }
            }

            return string.Join("\n", texts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
