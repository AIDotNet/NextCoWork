using System.Text;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Inspector;

public class TerminalInputPumpTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task The_first_key_goes_out_immediately_and_keys_typed_during_a_write_are_merged_into_one_command()
    {
        var sent = new List<string>();
        var gates = new Queue<TaskCompletionSource<bool>>();
        var pump = new TerminalInputPump(async data =>
        {
            sent.Add(Encoding.UTF8.GetString(data));
            return gates.TryDequeue(out var g) ? await g.Task : true;
        });
        var first = new TaskCompletionSource<bool>();
        gates.Enqueue(first);

        pump.Enqueue(B("l"));              // 没有写在途:立刻发(第一个字符零延迟)
        pump.Enqueue(B("s"));
        pump.Enqueue(B(" -la"));
        pump.Enqueue(B("\r"));
        Assert.Equal(["l"], sent);        // 写在途期间的输入攒着

        first.SetResult(true);
        await pump.Idle;

        Assert.Equal(["l", "s -la\r"], sent); // 合成一个命令,不是一次按键一个
        Assert.Equal(2, pump.Sends);
    }

    [Fact]
    public async Task A_big_paste_is_cut_into_chunks_of_at_most_the_device_limit_in_order()
    {
        var sent = new List<int>();
        var pump = new TerminalInputPump(data =>
        {
            sent.Add(data.Length);
            return Task.FromResult(true);
        }, maxChunk: 10);
        var gate = new TaskCompletionSource<bool>();
        var holder = new TerminalInputPump(async data =>
        {
            sent.Add(data.Length);
            return await gate.Task;
        }, maxChunk: 10);

        holder.Enqueue(B("x"));
        holder.Enqueue(B(new string('y', 25)));
        gate.SetResult(true);
        await holder.Idle;
        pump.Enqueue(B(new string('z', 25)));
        await pump.Idle;

        Assert.Equal([1, 10, 10, 5, 10, 10, 5], sent);
    }

    [Fact]
    public async Task A_failed_write_drops_what_was_waiting_and_reports_instead_of_replaying_stale_keys()
    {
        var sent = new List<string>();
        var gate = new TaskCompletionSource<bool>();
        var failed = 0;
        var pump = new TerminalInputPump(async data =>
        {
            sent.Add(Encoding.UTF8.GetString(data));
            return await gate.Task;
        });
        pump.Failed += () => failed++;

        pump.Enqueue(B("a"));
        pump.Enqueue(B("b"));
        gate.SetResult(false); // 断线
        await pump.Idle;

        Assert.Equal(["a"], sent); // "b" 没有被重放
        Assert.Equal(1, failed);

        // 之后重连又能写:新的输入照常发
        var ok = new TerminalInputPump(_ => Task.FromResult(true));
        ok.Enqueue(B("c"));
        await ok.Idle;
        Assert.Equal(1, ok.Sends);
    }

    [Fact]
    public async Task Empty_input_is_ignored_and_clear_drops_pending_keys()
    {
        var sent = new List<string>();
        var gate = new TaskCompletionSource<bool>();
        var pump = new TerminalInputPump(async data =>
        {
            sent.Add(Encoding.UTF8.GetString(data));
            return await gate.Task;
        });

        pump.Enqueue(ReadOnlySpan<byte>.Empty);
        Assert.Empty(sent);
        pump.Enqueue(B("a"));
        pump.Enqueue(B("b"));
        pump.Clear();
        gate.SetResult(true);
        await pump.Idle;

        Assert.Equal(["a"], sent);
    }
}

public class TerminalResizePumpTests
{
    [Fact]
    public async Task Only_the_latest_size_is_sent_while_one_is_in_flight_and_duplicates_are_skipped()
    {
        var sent = new List<(int, int)>();
        var gate = new TaskCompletionSource<bool>();
        var first = true;
        var pump = new TerminalResizePump(async (c, r) =>
        {
            sent.Add((c, r));
            if (first)
            {
                first = false;
                return await gate.Task;
            }

            return true;
        });
        pump.Known(80, 24);

        pump.Request(80, 24);           // 和已知的相同:不发
        pump.Request(100, 30);          // 发出,在途
        pump.Request(110, 31);
        pump.Request(120, 40);          // 在途期间只留最后一个
        gate.SetResult(true);
        await pump.Idle;

        Assert.Equal([(100, 30), (120, 40)], sent);
    }

    [Fact]
    public async Task A_failed_resize_is_not_retried_in_a_loop()
    {
        var calls = 0;
        var pump = new TerminalResizePump((_, _) =>
        {
            calls++;
            return Task.FromResult(false);
        });

        pump.Request(100, 30);
        await pump.Idle;

        Assert.Equal(1, calls);
        pump.Request(100, 30); // 下一次(例如重连后)还能再试
        await pump.Idle;
        Assert.Equal(2, calls);
        pump.Request(0, 0);    // 无效的大小不发
        Assert.Equal(2, calls);
    }
}

public class TerminalOutputRelayTests
{
    [Fact]
    public void Output_before_a_surface_exists_is_kept_and_replayed_in_order_on_attach()
    {
        var relay = new TerminalOutputRelay();
        var surface = new FakeSurface();

        relay.OnSnapshot(Encoding.UTF8.GetBytes("old"), false);
        relay.OnData(Encoding.UTF8.GetBytes("A"));
        relay.OnData(Encoding.UTF8.GetBytes("B"));
        relay.Attach(surface);
        relay.OnData(Encoding.UTF8.GetBytes("C"));

        Assert.Equal(["reset", "old", "A", "B", "C"], surface.Log);
    }

    [Fact]
    public void A_snapshot_replaces_everything_that_was_waiting_and_resets_a_live_surface()
    {
        var relay = new TerminalOutputRelay();
        relay.OnData(Encoding.UTF8.GetBytes("stale"));
        relay.OnSnapshot(Encoding.UTF8.GetBytes("full"), true);
        var surface = new FakeSurface();
        relay.Attach(surface);

        Assert.Equal(["reset", "full"], surface.Log);

        // 游标续不上:设备再给整个缓冲 → 显示面重置后重写
        relay.OnSnapshot(Encoding.UTF8.GetBytes("again"), false);
        Assert.Equal(["reset", "full", "reset", "again"], surface.Log);
    }

    [Fact]
    public void A_resumed_cursor_appends_the_missed_batches_without_resetting()
    {
        var relay = new TerminalOutputRelay();
        var surface = new FakeSurface();
        relay.Attach(surface);
        relay.OnSnapshot(Encoding.UTF8.GetBytes("$ "), false);

        // 断线重连后按游标续上:只补发错过的批,显示面不重置
        relay.OnData(Encoding.UTF8.GetBytes("ls\r\n"));
        relay.OnData(Encoding.UTF8.GetBytes("a b\r\n"));

        Assert.Equal(["reset", "$ ", "ls\r\n", "a b\r\n"], surface.Log);
        Assert.Equal(1, surface.Log.Count(l => l == "reset"));
    }

    [Fact]
    public void Too_much_unseen_output_is_dropped_and_asks_for_a_resubscribe_when_a_surface_attaches()
    {
        var relay = new TerminalOutputRelay();
        var resync = 0;
        relay.Resync += () => resync++;
        var chunk = new byte[TerminalOutputRelay.PendingLimit / 2 + 1];

        relay.OnData(chunk);
        relay.OnData(chunk); // 超限
        relay.OnData(chunk); // 溢出之后不再攒
        var surface = new FakeSurface();
        relay.Attach(surface);

        Assert.Empty(surface.Log); // 不放出被截断的一段(屏幕会乱),改为重新订阅拿整个缓冲
        Assert.Equal(1, resync);
    }

    [Fact]
    public void Exit_and_gone_are_forwarded()
    {
        var relay = new TerminalOutputRelay();
        (int?, int?, string)? exit = null;
        var gone = false;
        relay.Exited += (c, s, r) => exit = (c, s, r);
        relay.Gone += () => gone = true;

        relay.OnExit(3, null, "Exited");
        relay.OnGone();

        Assert.Equal((3, null, "Exited"), exit);
        Assert.True(gone);
    }
}

public class TerminalPhaseTests
{
    private static TerminalView View(string status = "running", bool known = true, long created = 1)
    {
        var dto = new TerminalDto("t", "p", new TerminalEnvDto("local", "local", "local", "local"), "t", "/", "/bin/sh", 80, 24, status, created);
        return new TerminalView(dto, 0, known);
    }

    [Fact]
    public void Phase_is_derived_in_one_place()
    {
        Assert.Equal(TerminalPhase.AwaitingApproval, TerminalPhases.Of(null, true, approvalPending: true, gone: false));
        Assert.Equal(TerminalPhase.Starting, TerminalPhases.Of(null, true, false, false));
        Assert.Equal(TerminalPhase.Live, TerminalPhases.Of(View(), true, false, false));
        Assert.Equal(TerminalPhase.Exited, TerminalPhases.Of(View("exited"), true, false, false));
        Assert.Equal(TerminalPhase.Gone, TerminalPhases.Of(View(), true, false, gone: true));
    }

    [Fact]
    public void A_disconnected_device_makes_a_running_terminal_unknown_but_never_ended()
    {
        Assert.Equal(TerminalPhase.Unknown, TerminalPhases.Of(View(), deviceConnected: false, false, false));
        Assert.Equal(TerminalPhase.Unknown, TerminalPhases.Of(View(known: false), deviceConnected: true, false, false));
    }

    [Fact]
    public void An_exited_terminal_stays_exited_whatever_the_connection_does()
    {
        // 终态压过一切(规则 6):退出之后断线,还是「已退出」
        Assert.Equal(TerminalPhase.Exited, TerminalPhases.Of(View("exited", known: false), deviceConnected: false, false, false));
    }

    [Fact]
    public void Exit_is_described_structurally()
    {
        Assert.Equal(new TerminalExit(TerminalExitKind.Code, 2), TerminalExit.Of(2, null, "Exited"));
        Assert.Equal(new TerminalExit(TerminalExitKind.Signal, 15), TerminalExit.Of(null, 15, "Exited"));
        Assert.Equal(new TerminalExit(TerminalExitKind.Killed), TerminalExit.Of(null, 15, "Killed"));
        Assert.Equal(new TerminalExit(TerminalExitKind.ConnectionLost), TerminalExit.Of(null, null, "ConnectionLost"));
        Assert.Equal(new TerminalExit(TerminalExitKind.Plain), TerminalExit.Of(null, null, null));
    }
}

public class TerminalPanelTests
{
    private static readonly ProjectRef Project = new("local", "p1");

    private static (TerminalPanelViewModel Panel, FakeTerminals Terminals, ClientStores Stores) Create(DeviceConnection connection = DeviceConnection.Connected)
    {
        var stores = new ClientStores();
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", new DeviceInfo("local", "本机", DeviceKind.Local, connection, null)) });
        var terminals = new FakeTerminals { Stores = stores };
        var panel = new TerminalPanelViewModel(stores, terminals);
        panel.Bind(Project);
        return (panel, terminals, stores);
    }

    private static void Disconnect(ClientStores stores)
    {
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Offline }) });
        stores.Terminals.Update(m => TerminalReducer.MarkDeviceUnknown(m, "local"));
    }

    [Fact]
    public async Task Opening_the_page_lists_existing_terminals_and_attaches_the_selected_one()
    {
        var (panel, terminals, _) = Create();
        terminals.Existing.Add(terminals.Make("t1"));
        terminals.Existing.Add(terminals.Make("t2"));

        await panel.SetVisibleAsync(true);

        Assert.Equal(["t1", "t2"], panel.Tabs.Select(t => t.Id));
        Assert.Equal("t2", panel.Active!.Id);
        Assert.Contains("attach:t2", terminals.Calls);
        Assert.Equal("本机 · local", panel.Active.EnvLabel); // 标题上是「设备 · 环境」
        Assert.True(panel.Active.IsLive);
    }

    [Fact]
    public async Task Switching_tabs_detaches_the_old_one_and_attaches_the_new_one_and_hiding_detaches_everything()
    {
        var (panel, terminals, _) = Create();
        terminals.Existing.Add(terminals.Make("t1"));
        terminals.Existing.Add(terminals.Make("t2"));
        await panel.SetVisibleAsync(true);
        terminals.Calls.Clear();

        panel.ActivateCommand.Execute(panel.Tabs[0]);
        await Task.Yield();
        Assert.Equal(["detach:t2", "attach:t1"], terminals.Calls);

        terminals.Calls.Clear();
        await panel.SetVisibleAsync(false);
        Assert.Contains("detach:t1", terminals.Calls); // 页签不可见:退订输出(设备上的终端照常运行)
        Assert.Empty(terminals.Sinks);
    }

    [Fact]
    public async Task A_local_terminal_is_created_directly_with_the_last_known_size_and_selected()
    {
        var (panel, terminals, _) = Create();
        await panel.SetVisibleAsync(true);
        panel.SetGrid(132, 43);

        await panel.NewTerminalCommand.ExecuteAsync(null);

        Assert.Equal(["list", "prepare:132x43"], terminals.Calls.Take(2));
        Assert.Equal("t-new", panel.Active!.Id);
        Assert.True(panel.Active.IsLive);
        Assert.Contains("attach:t-new", terminals.Calls);
    }

    [Fact]
    public async Task A_remote_terminal_waits_for_approval_showing_the_environment_then_creates_with_the_credential()
    {
        var (panel, terminals, _) = Create();
        terminals.NextIntent = new TerminalIntentDto("intent-1", "t-ssh", "p1", new TerminalEnvDto("local", "dev-server", "dev-server", "ssh"), "/srv/api", "/bin/bash", 0);
        await panel.SetVisibleAsync(true);

        await panel.NewTerminalCommand.ExecuteAsync(null);

        var tab = panel.Active!;
        Assert.True(tab.IsAwaitingApproval);
        Assert.Equal("本机 · dev-server", tab.EnvLabel); // 审批卡片带执行环境标识(设备 + 环境)
        Assert.True(tab.IsRemote);
        Assert.Equal("/srv/api", tab.Cwd);
        Assert.DoesNotContain(terminals.Calls, c => c.StartsWith("create", StringComparison.Ordinal)); // 批准前什么都没启动
        Assert.DoesNotContain("attach:t-ssh", terminals.Calls);

        await panel.ApproveCommand.ExecuteAsync(tab);

        Assert.Contains("approve:True", terminals.Calls);
        Assert.Contains("create:t-ssh:cred-1", terminals.Calls);
        Assert.False(tab.IsAwaitingApproval);
        Assert.Same(tab, panel.Active);
        Assert.Contains("attach:t-ssh", terminals.Calls);
        Assert.True(tab.IsLive);
    }

    [Fact]
    public async Task Denying_removes_the_pending_tab_without_creating_anything()
    {
        var (panel, terminals, _) = Create();
        terminals.NextIntent = new TerminalIntentDto("intent-1", "t-ssh", "p1", new TerminalEnvDto("local", "dev-server", "dev-server", "ssh"), "/srv", "/bin/bash", 0);
        await panel.SetVisibleAsync(true);
        await panel.NewTerminalCommand.ExecuteAsync(null);

        await panel.DenyCommand.ExecuteAsync(panel.Active!);

        Assert.Empty(panel.Tabs);
        Assert.Contains("approve:False", terminals.Calls);
        Assert.DoesNotContain(terminals.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keys_typed_while_the_terminal_is_busy_are_merged_and_resize_is_forwarded()
    {
        var (panel, terminals, _) = Create();
        terminals.Existing.Add(terminals.Make("t1"));
        await panel.SetVisibleAsync(true);
        var surface = new FakeSurface();
        var first = new TaskCompletionSource<bool>();
        terminals.WriteGates.Enqueue(first);
        panel.Active!.AttachSurface(surface);

        surface.Type("e");
        surface.Type("c");
        surface.Type("ho");
        surface.Resize(120, 40);
        Assert.Single(terminals.Writes);
        first.SetResult(true);
        await panel.Active.InputIdle;

        Assert.Equal(["e", "cho"], terminals.Writes.Select(Encoding.UTF8.GetString));
        Assert.Contains((120, 40), terminals.Resizes);
        Assert.Contains((100, 30), terminals.Resizes); // 挂上显示面时先同步一次它的大小
    }

    [Fact]
    public async Task Disconnect_shows_unknown_stops_input_and_reconnect_goes_back_to_live()
    {
        var (panel, terminals, stores) = Create();
        terminals.Existing.Add(terminals.Make("t1"));
        await panel.SetVisibleAsync(true);
        var surface = new FakeSurface();
        panel.Active!.AttachSurface(surface);
        var tab = panel.Active;

        Disconnect(stores);

        Assert.True(tab.IsUnknown);   // 不判成结束
        Assert.False(tab.IsExited);
        Assert.False(tab.CanType);
        surface.Type("x");             // 断线时的输入不攒、不发
        await tab.InputIdle;
        Assert.Empty(terminals.Writes);

        // 重连:设备重新给元数据(列表),状态恢复;游标续传由连接层完成,输出照常进 Relay
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Connected }) });
        await panel.LoadAsync();

        Assert.True(tab.IsLive);
        Assert.True(tab.CanType);
        tab.Relay.OnData(Encoding.UTF8.GetBytes("resumed"));
        Assert.Contains("resumed", surface.Log); // 补发的输出追加,不重置
        Assert.DoesNotContain(surface.Log.Skip(1), l => l == "reset");
        surface.Type("y");
        await tab.InputIdle;
        Assert.Equal(["y"], terminals.Writes.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public async Task An_exit_is_terminal_a_late_running_update_does_not_bring_it_back_and_a_disconnect_does_not_change_it()
    {
        var (panel, terminals, stores) = Create();
        var running = terminals.Make("t1");
        terminals.Existing.Add(running);
        await panel.SetVisibleAsync(true);
        var tab = panel.Active!;

        stores.Terminals.Update(m => TerminalReducer.ApplyExit(m, "local", "t1", new TerminalExitDto(2, null, "Exited"), 9));
        Assert.True(tab.IsExited);
        Assert.Equal(new TerminalExit(TerminalExitKind.Code, 2), tab.Exit);

        stores.Terminals.Update(m => TerminalReducer.ApplyInfo(m, "local", running, 5)); // 迟到的「运行中」元数据
        Assert.True(tab.IsExited);

        Disconnect(stores);
        Assert.True(tab.IsExited); // 终态压过一切
        Assert.False(tab.CanType);
        Assert.False(tab.CanKill);
    }

    [Fact]
    public async Task Close_kills_removes_the_tab_and_kill_keeps_it_as_exited_history()
    {
        var (panel, terminals, _) = Create();
        terminals.Existing.Add(terminals.Make("t1"));
        terminals.Existing.Add(terminals.Make("t2"));
        await panel.SetVisibleAsync(true);

        await panel.KillCommand.ExecuteAsync(panel.Tabs[0]);
        Assert.Contains("kill:t1", terminals.Calls);
        Assert.Equal(2, panel.Tabs.Count);

        await panel.CloseCommand.ExecuteAsync(panel.Tabs[1]);
        Assert.Contains("close:t2", terminals.Calls);
        Assert.Equal(["t1"], panel.Tabs.Select(t => t.Id));
        Assert.Equal("t1", panel.Active!.Id);
    }

    [Fact]
    public async Task Creating_is_blocked_while_the_device_is_not_connected()
    {
        var (panel, _, _) = Create(DeviceConnection.Offline);

        Assert.False(panel.CanCreate);
        await panel.NewTerminalCommand.ExecuteAsync(null);
        Assert.True(panel.IsEmpty);
    }

    [Fact]
    public async Task Switching_project_detaches_the_old_terminals_and_shows_the_new_projects_list()
    {
        var (panel, terminals, _) = Create();
        terminals.Existing.Add(terminals.Make("t1"));
        await panel.SetVisibleAsync(true);

        panel.Bind(new ProjectRef("local", "other"));
        await Task.Yield();

        Assert.Contains("detach:t1", terminals.Calls);
        Assert.Empty(panel.Tabs);
    }
}
