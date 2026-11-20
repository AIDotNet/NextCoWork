using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Client.ViewModels.Mobile;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop.Conversation;
using Ncw.UI.Views.Mobile;

namespace Ncw.UI.Tests;

public sealed class WorkflowCanvasTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 17, 0, 0, TimeSpan.FromHours(8));

    [AvaloniaFact]
    public void WorkflowRunCard_renders_interactive_inline_canvas_with_direction_and_node_details()
    {
        var stores = MockWorkspace.Create(Now);
        var deviceId = stores.Workspace.State.Devices.Keys.First();
        var def = new WorkflowDefinition(
            Id: "inline-demo",
            Name: "Inline PR Pipeline",
            Nodes:
            [
                new("start", WorkflowNodeKind.Trigger, "Trigger"),
                new("review", WorkflowNodeKind.Agent, "Review"),
                new("out", WorkflowNodeKind.Output, "Summary"),
            ],
            Edges:
            [
                new("e1", "start", "review"),
                new("e2", "review", "out"),
            ]);
        var defJson = JsonSerializer.Serialize(def, WorkflowJsonContext.Default.WorkflowDefinition);

        stores.Workflows.Update(s => WorkflowReducer.ApplySnapshot(s, deviceId, new WorkflowRunSnapshotDto(
            RunId: "wfr-inline-1",
            WorkspaceId: "ws-1",
            Title: def.Name,
            Status: "running",
            DefinitionJson: defJson,
            Nodes:
            [
                new("start", "trigger", "done", 1, OutputText: "started"),
                new("review", "agent", "running", 1),
                new("out", "output", "pending", 1),
            ],
            Edges:
            [
                new("e1", "active"),
                new("e2", "idle"),
            ],
            EnvironmentId: "local",
            Seq: 2)));

        var vm = new WorkflowRunCardViewModel(stores, deviceId, "wfr-inline-1");
        var card = new WorkflowRunCard { DataContext = vm };
        var window = new Window { Width = 760, Height = 420, Content = card };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var canvas = card.GetVisualDescendants().OfType<NcwWorkflowCanvas>().Single();
        Assert.False(canvas.IsEditable);
        Assert.True(canvas.IsPanZoomEnabled);
        Assert.True(canvas.ShowChrome);
        Assert.Equal("1/3", vm.ProgressText);
        Assert.Equal("Inline PR Pipeline", vm.Title);

        vm.ToggleLayoutDirectionCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WorkflowLayoutDirection.TopToBottom, canvas.LayoutDirection);

        vm.SelectedNodeId = "start";
        Assert.True(vm.HasSelectedNode);
        Assert.Equal("Trigger", vm.SelectedNodeDef?.Title);
        Assert.Equal("started", vm.SelectedNodeRun?.OutputText);

        window.Close();
    }

    [AvaloniaFact]
    public void Mobile_WorkflowRunPage_renders_overview_canvas_and_topological_timeline()
    {
        var stores = MockWorkspace.Create(Now);
        var deviceId = stores.Workspace.State.Devices.Keys.First();
        var def = new WorkflowDefinition(
            Id: "mobile-demo",
            Name: "Mobile Deploy Check",
            Nodes:
            [
                new("start", WorkflowNodeKind.Trigger, "Trigger"),
                new("build", WorkflowNodeKind.Tool, "Build Artifact"),
                new("approve", WorkflowNodeKind.Approval, "Approve Release"),
                new("done", WorkflowNodeKind.Output, "Summary"),
            ],
            Edges:
            [
                new("e1", "start", "build"),
                new("e2", "build", "approve"),
                new("e3", "approve", "done"),
            ]);
        var defJson = JsonSerializer.Serialize(def, WorkflowJsonContext.Default.WorkflowDefinition);

        stores.Workflows.Update(s => WorkflowReducer.ApplySnapshot(s, deviceId, new WorkflowRunSnapshotDto(
            RunId: "wfr-mob-1",
            WorkspaceId: "ws-1",
            Title: def.Name,
            Status: "waiting_approval",
            DefinitionJson: defJson,
            Nodes:
            [
                new("start", "trigger", "done", 1, StartedAt: 1000, FinishedAt: 1050),
                new("build", "tool", "done", 1, Summary: "0 warnings", StartedAt: 1050, FinishedAt: 2550),
                new("approve", "approval", "waiting_approval", 1, Summary: "Confirm production deploy"),
                new("done", "output", "pending", 1),
            ],
            Edges:
            [
                new("e1", "active"),
                new("e2", "active"),
                new("e3", "idle"),
            ],
            Seq: 4)));

        var vm = new MobileWorkflowViewModel(stores, deviceId, "wfr-mob-1");
        var page = new WorkflowRunPage { DataContext = vm };
        var window = new Window { Width = 390, Height = 844, Content = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var canvas = page.GetVisualDescendants().OfType<NcwWorkflowCanvas>().Single();
        Assert.False(canvas.IsEditable);
        Assert.True(canvas.IsPanZoomEnabled);
        Assert.Equal("2/4", vm.ProgressText);
        Assert.Equal(4, vm.Steps.Count);
        Assert.Equal(["start", "build", "approve", "done"], vm.Steps.Select(s => s.NodeId).ToArray());
        Assert.Equal("1.5s", vm.Steps[1].DurationText);

        window.Close();
    }

    [AvaloniaFact]
    public void RunWorkflow_tool_call_renders_single_inline_WorkflowRunCard_not_duplicated_and_not_folded()
    {
        var stores = MockWorkspace.Create(Now);
        var vm = new ShellViewModel(stores);
        var session = vm.Conversation.Session;
        Assert.NotNull(session);
        var key = session.Value;

        const string asstPartsJson = """
        [
          {
            "type": "tool_call",
            "callId": "call-wf-1",
            "name": "RunWorkflow",
            "input": {
              "title": "Parallel Verification",
              "steps": [
                { "id": "branch_a", "kind": "tool", "label": "Branch A", "tool": "Bash", "args": { "command": "echo A" } },
                { "id": "branch_b", "kind": "tool", "label": "Branch B", "tool": "Bash", "args": { "command": "echo B" } },
                { "id": "join_all", "kind": "join", "label": "Join", "depends_on": ["branch_a", "branch_b"] },
                { "id": "final", "kind": "output", "label": "Final", "depends_on": ["join_all"] }
              ]
            }
          },
          {
            "type": "tool_result",
            "callId": "call-wf-1",
            "name": "RunWorkflow",
            "isError": false,
            "output": {
              "content": "Workflow completed",
              "details": {
                "type": "workflow_run",
                "runId": "wfr-transcript-1",
                "title": "Parallel Verification",
                "status": "completed"
              }
            }
          },
          { "type": "text", "text": "All 4 nodes completed." }
        ]
        """;

        var userMsg = new MessageDto("m-wf-u", "user", Now.ToUnixTimeMilliseconds(), false,
            """[{"type":"text","text":"Run workflow test"}]""");
        var asstMsg = new MessageDto("m-wf-a", "assistant", Now.ToUnixTimeMilliseconds() + 100, false, asstPartsJson);
        var nowMs = Now.ToUnixTimeMilliseconds();
        var meta = new SessionDto(key.SessionId, "nextcowork", null, "user", "Workflow Test", null, "default", null, "code", "auto", false, false, nowMs, nowMs);
        var snapshot = new SessionTopicState(new SessionPageDto(meta, [userMsg, asstMsg], false, [], [], []), new QueueDto(key.SessionId, 0, []), null);

        stores.Conversations.Update(s => ConversationReducer.ApplySession(s, key.DeviceId, snapshot));

        var view = new ConversationView { DataContext = vm.Conversation };
        var window = new Window { Width = 960, Height = 820, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var cardVm = vm.Conversation.Rows.OfType<WorkflowRunCardViewModel>().Single();
        Assert.Equal("Parallel Verification", cardVm.Title);
        Assert.Equal("wfr-transcript-1", cardVm.RunId);
        Assert.Equal("4/4", cardVm.ProgressText);
        Assert.NotNull(cardVm.Definition);
        Assert.Equal(4, cardVm.Definition!.Nodes.Count);
        Assert.Equal(3, cardVm.Definition.Edges.Count);

        // Must render exactly ONE NcwWorkflowCanvas in the entire ConversationView (no duplicate bottom panel)
        var canvases = view.GetVisualDescendants().OfType<NcwWorkflowCanvas>().ToList();
        Assert.Single(canvases);
        Assert.False(canvases[0].IsEditable);
        Assert.True(canvases[0].IsPanZoomEnabled);
        Assert.True(canvases[0].ShowChrome);
        Assert.Equal(4, canvases[0].Definition!.Nodes.Count);

        window.Close();
    }
}
