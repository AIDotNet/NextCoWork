using System.Text.Json;

namespace Ncw.Agent.Plugins.Tests;

/// <summary>
/// 崩溃隔离与预算(AGENTS.md §6:JsLimits + JsProfile.Hardened,每个插件一个引擎):死循环 / 内存炸弹 / 激活失败 / 编译失败都只让<b>这个插件</b>进入 faulted / error 并上报诊断,
/// 不拖垮宿主、不影响别的插件。
/// </summary>
public sealed class IsolationTests : IAsyncDisposable
{
    private PluginFx? _fx;

    private PluginFx Fx(PluginRuntimeLimits? limits = null) => _fx ??= new PluginFx(b => b.Limits = limits ?? Small);

    private static readonly PluginRuntimeLimits Small = new()
    {
        ActivateTimeout = TimeSpan.FromSeconds(3),
        CommandTimeout = TimeSpan.FromSeconds(3),
        ToolTimeout = TimeSpan.FromSeconds(3),
        HookTimeout = TimeSpan.FromMilliseconds(500),
        MaxInstructionsPerCall = 3_000_000,
        MaxTrackedBytes = 16L * 1024 * 1024,
        InteractionTimeout = TimeSpan.FromSeconds(3),
    };

    public ValueTask DisposeAsync() => _fx?.DisposeAsync() ?? ValueTask.CompletedTask;

    private const string Commands = """ "contributes": { "commands": [{ "command": "acme.x.go", "title": "%a%" }, { "command": "acme.x.ok", "title": "%b%" }], "tools": [{ "name": "t", "title": "%t%" }] } """;

    private static byte[] Plugin(string name, string body, string? manifest = null) => PluginFx.MinimalZip(name, $$"""
        import * as ncw from 'nextcowork'
        {{body}}
        """, manifest ?? Commands.Replace("acme.x.", $"acme.{name}.", StringComparison.Ordinal));

    [Fact]
    public async Task A_busy_loop_is_stopped_by_the_instruction_budget_the_plugin_is_faulted_and_other_plugins_are_unaffected()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(PluginFx.FixtureZip("acme.think"));
        await fx.InstallEnableAsync(Plugin("spin", """
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.spin.go', async () => { while (true) {} }))
              c.subscriptions.push(ncw.commands.registerCommand('acme.spin.ok', async () => 1))
              c.subscriptions.push(ncw.tools.registerTool('t', { description: 'd', inputSchema: { type: 'object' }, async invoke() { return 'x' } }))
            }
            """));
        Assert.NotNull(fx.Registry.Find("plugin__acme_spin__t"));

        var e = await Assert.ThrowsAsync<PluginException>(() => fx.Manager.RunCommandAsync("acme.spin", "acme.spin.go", null, null));

        Assert.Equal(PluginErrorKeys.Faulted, e.Key);
        Assert.Equal("instructions", e.Parameters!["reason"]);
        var spin = fx.Plugin("acme.spin");
        Assert.Equal(PluginStatus.Faulted, spin.Status);
        var diagnostic = Assert.Single(spin.Diagnostics, d => d.Key == PluginDiagnosticKeys.Faulted);
        Assert.Equal("instructions", diagnostic.Params!["reason"]);
        Assert.Equal("error", diagnostic.Level);
        Assert.Null(fx.Registry.Find("plugin__acme_spin__t")); // 贡献撤下
        Assert.Empty(spin.RegisteredCommands);
        // 别的插件照常
        Assert.Equal(PluginStatus.Active, fx.Plugin("acme.think").Status);
        Assert.StartsWith("Thought recorded", ToolHarness.TextOf(await ToolHarness.CallAsync(fx, "acme.think", "think", """{"task_goal":"g","thought":"t"}""")), StringComparison.Ordinal);
        // faulted 的插件再被调:稳定的错误,不是崩溃
        var again = await Assert.ThrowsAsync<PluginException>(() => fx.Manager.RunCommandAsync("acme.spin", "acme.spin.ok", null, null));
        Assert.Equal(PluginErrorKeys.Faulted, again.Key);
    }

    [Fact]
    public async Task A_faulted_plugin_comes_back_when_the_user_enables_it_again_with_a_fresh_engine()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(Plugin("flaky", """
            let spun = false
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.flaky.go', async (args) => { if (args && args.spin) { while (true) {} } }))
              c.subscriptions.push(ncw.commands.registerCommand('acme.flaky.ok', async () => ncw.storage.global.set('alive', 'yes')))
            }
            """, """ "permissions": ["storage"], "contributes": { "commands": [{ "command": "acme.flaky.go", "title": "%a%" }, { "command": "acme.flaky.ok", "title": "%b%" }] } """));
        await Assert.ThrowsAsync<PluginException>(() => fx.Manager.RunCommandAsync("acme.flaky", "acme.flaky.go", """{"spin":true}""", null));
        Assert.Equal(PluginStatus.Faulted, fx.Plugin("acme.flaky").Status);

        var revived = await fx.Manager.SetEnabledAsync("acme.flaky", true);

        Assert.Equal(PluginStatus.Active, revived.Status);
        Assert.DoesNotContain(revived.Diagnostics, d => d.Key == PluginDiagnosticKeys.Faulted); // 上一轮的故障诊断随重新启用清掉
        await fx.Manager.RunCommandAsync("acme.flaky", "acme.flaky.ok", null, null);
        Assert.Equal("yes", fx.Stored("acme.flaky", "alive"));
    }

    [Fact]
    public async Task Without_an_instruction_budget_the_wall_clock_timeout_stops_a_busy_loop()
    {
        var fx = Fx(Small with { MaxInstructionsPerCall = 0, CommandTimeout = TimeSpan.FromMilliseconds(400) });
        await fx.InstallEnableAsync(Plugin("clock", """
            export function activate(c) { c.subscriptions.push(ncw.commands.registerCommand('acme.clock.go', async () => { while (true) {} })) }
            """, """ "contributes": { "commands": [{ "command": "acme.clock.go", "title": "%a%" }] } """));

        var e = await Assert.ThrowsAsync<PluginException>(() => fx.Manager.RunCommandAsync("acme.clock", "acme.clock.go", null, null));

        Assert.Equal(PluginErrorKeys.Faulted, e.Key);
        Assert.Equal("timeout", e.Parameters!["reason"]);
        Assert.Equal(PluginStatus.Faulted, fx.Plugin("acme.clock").Status);
    }

    [Fact]
    public async Task A_memory_bomb_is_stopped_by_the_allocation_budget()
    {
        var fx = Fx(Small with { MaxTrackedBytes = 8L * 1024 * 1024 });
        await fx.InstallEnableAsync(Plugin("bomb", """
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.bomb.go', async () => { const keep = []; for (;;) keep.push(new Array(10000).fill('x'.repeat(100))) }))
            }
            """, """ "contributes": { "commands": [{ "command": "acme.bomb.go", "title": "%a%" }] } """));

        var e = await Assert.ThrowsAsync<PluginException>(() => fx.Manager.RunCommandAsync("acme.bomb", "acme.bomb.go", null, null));

        Assert.Equal(PluginErrorKeys.Faulted, e.Key);
        Assert.Equal("memory", e.Parameters!["reason"]);
        Assert.Equal(PluginStatus.Faulted, fx.Plugin("acme.bomb").Status);
    }

    [Fact]
    public async Task Runaway_recursion_hits_the_stack_budget()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(Plugin("deep", """
            function down(n) { return down(n + 1) + 1 }
            export function activate(c) { c.subscriptions.push(ncw.commands.registerCommand('acme.deep.go', async () => down(0))) }
            """, """ "contributes": { "commands": [{ "command": "acme.deep.go", "title": "%a%" }] } """));

        var e = await Assert.ThrowsAsync<PluginException>(() => fx.Manager.RunCommandAsync("acme.deep", "acme.deep.go", null, null));

        Assert.True(e.Key == PluginErrorKeys.Faulted || e.Key == PluginErrorKeys.CommandFailed, e.Key); // 栈深限制:要么当故障,要么当 RangeError 抛回给插件——都不是宿主崩溃
        Assert.NotEmpty(fx.Manager.Snapshot().Plugins);
    }

    [Fact]
    public async Task A_busy_loop_in_a_tool_call_returns_a_tool_error_to_the_model_and_the_run_survives()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(Plugin("looptool", """
            export function activate(c) {
              c.subscriptions.push(ncw.tools.registerTool('t', { description: 'd', inputSchema: { type: 'object' }, async invoke() { for (;;) {} } }))
            }
            """));

        var result = await ToolHarness.CallAsync(fx, "acme.looptool", "t", "{}");

        Assert.True(result.IsError);
        Assert.Contains("execution budget", ToolHarness.TextOf(result), StringComparison.Ordinal);
        Assert.Equal(PluginStatus.Faulted, fx.Plugin("acme.looptool").Status);
        Assert.Null(fx.Registry.Find("plugin__acme_looptool__t"));
    }

    [Fact]
    public async Task A_busy_loop_during_activation_leaves_the_plugin_faulted_with_a_diagnostic()
    {
        var fx = Fx();

        var plugin = await fx.InstallEnableAsync(Plugin("loopstart", "export function activate() { while (true) {} }"));

        Assert.Equal(PluginStatus.Faulted, plugin.Status);
        Assert.Contains(plugin.Diagnostics, d => d.Key == PluginDiagnosticKeys.Faulted && d.Params!["reason"] == "instructions");
    }

    [Fact]
    public async Task A_module_level_busy_loop_is_also_stopped()
    {
        var fx = Fx();

        var plugin = await fx.InstallEnableAsync(Plugin("loopmodule", "while (true) {}"));

        Assert.Equal(PluginStatus.Faulted, plugin.Status);
    }

    [Fact]
    public async Task An_activate_that_throws_makes_the_plugin_an_error_with_the_message_as_a_diagnostic()
    {
        var fx = Fx();

        var plugin = await fx.InstallEnableAsync(Plugin("badstart", "export function activate() { throw new Error('cannot start: no config') }"));

        Assert.Equal(PluginStatus.Error, plugin.Status);
        var diagnostic = Assert.Single(plugin.Diagnostics, d => d.Level == "error");
        Assert.Contains("cannot start: no config", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Syntax_errors_are_compile_diagnostics_with_line_and_column_not_host_crashes()
    {
        var fx = Fx();

        var plugin = await fx.InstallEnableAsync(PluginFx.MinimalZip("broken", "export function activate( {\n  nope\n"));

        Assert.Equal(PluginStatus.Error, plugin.Status);
        var diagnostic = Assert.Single(plugin.Diagnostics, d => d.Key == PluginDiagnosticKeys.CompileFailed);
        Assert.Matches(@"\d+:\d+", diagnostic.Message);
    }

    [Fact]
    public async Task A_missing_entry_file_after_install_is_a_load_error()
    {
        var fx = Fx();
        await fx.Manager.InstallZipAsync(PluginFx.MinimalZip("vanish", "export function activate() {}"));
        File.Delete(Path.Combine(fx.PluginRoot, "acme.vanish", "main.js"));
        await fx.Manager.DisposeAsync();

        await using var again = new PluginManager(new PluginManagerOptions { Environment = fx.Env, Root = fx.P(fx.PluginRoot), Kv = fx.Kv, Tools = fx.Registry });
        await again.StartAsync();

        var plugin = again.Snapshot().Plugins.Single();
        Assert.Equal(PluginStatus.Error, plugin.Status);
        Assert.Contains(plugin.Diagnostics, d => d.Key == PluginDiagnosticKeys.MainMissing);
    }

    [Fact]
    public async Task Es_modules_are_hardened_eval_and_function_are_disabled_and_there_is_no_node_api()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(Plugin("hardened", """
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.hardened.go', async () => {
                const probe = (f) => { try { return String(f()) } catch (e) { return e.name } }
                await ncw.storage.global.set('report', JSON.stringify({
                  eval: probe(() => eval('1+1')),
                  fn: probe(() => new Function('return 1')()),
                  process: typeof process, require: typeof require, fetch: typeof fetch, xhr: typeof XMLHttpRequest, internals: typeof __ncw_dispatch + typeof __n_methods + typeof __n_log,
                  frozen: Object.isFrozen(Array.prototype), tamper: probe(() => { 'use strict'; Array.prototype.evil = 1; return Array.prototype.evil }),
                }))
              }))
            }
            """, """ "permissions": ["storage"], "contributes": { "commands": [{ "command": "acme.hardened.go", "title": "%a%" }] } """));

        await fx.Manager.RunCommandAsync("acme.hardened", "acme.hardened.go", null, null);

        using var report = JsonDocument.Parse(fx.Stored("acme.hardened", "report")!);
        var r = report.RootElement;
        Assert.Equal("EvalError", r.GetProperty("eval").GetString());
        Assert.Equal("EvalError", r.GetProperty("fn").GetString());
        Assert.Equal(("undefined", "undefined", "undefined", "undefined"), (r.GetProperty("process").GetString(), r.GetProperty("require").GetString(), r.GetProperty("fetch").GetString(), r.GetProperty("xhr").GetString()));
        Assert.Equal("undefinedundefinedundefined", r.GetProperty("internals").GetString()); // 前导留下的内部绑定对插件不可见
        Assert.True(r.GetProperty("frozen").GetBoolean());
        Assert.Equal("TypeError", r.GetProperty("tamper").GetString());
    }

    [Fact]
    public async Task Each_plugin_has_its_own_realm_globals_do_not_leak_between_plugins()
    {
        var fx = Fx();
        const string manifest = """ "permissions": ["storage"], "contributes": { "commands": [{ "command": "acme.NAME.go", "title": "%a%" }] } """;
        await fx.InstallEnableAsync(Plugin("alpha", """
            export function activate(c) { globalThis.secret = 'alpha-only'; c.subscriptions.push(ncw.commands.registerCommand('acme.alpha.go', async () => ncw.storage.global.set('seen', typeof globalThis.secret + ':' + globalThis.secret))) }
            """, manifest.Replace("NAME", "alpha", StringComparison.Ordinal)));
        await fx.InstallEnableAsync(Plugin("beta", """
            export function activate(c) { c.subscriptions.push(ncw.commands.registerCommand('acme.beta.go', async () => ncw.storage.global.set('seen', typeof globalThis.secret))) }
            """, manifest.Replace("NAME", "beta", StringComparison.Ordinal)));

        await fx.Manager.RunCommandAsync("acme.alpha", "acme.alpha.go", null, null);
        await fx.Manager.RunCommandAsync("acme.beta", "acme.beta.go", null, null);

        Assert.Equal("string:alpha-only", fx.Stored("acme.alpha", "seen"));
        Assert.Equal("undefined", fx.Stored("acme.beta", "seen"));
    }

    [Fact]
    public async Task Async_plugins_work_promises_all_timers_async_iteration_and_top_level_await()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(Plugin("asyncy", """
            const boot = await Promise.resolve('booted')
            async function* numbers() { for (let i = 1; i <= 3; i++) { await new Promise((r) => setTimeout(r, 5)); yield i } }
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.asyncy.go', async () => {
                const [a, b] = await Promise.all([ncw.storage.global.get('a'), new Promise((resolve) => setTimeout(() => resolve('timer'), 20))])
                let sum = 0
                for await (const n of numbers()) sum += n
                await ncw.storage.global.set('report', JSON.stringify({ boot, a, b, sum }))
              }))
            }
            """, """ "permissions": ["storage"], "contributes": { "commands": [{ "command": "acme.asyncy.go", "title": "%a%" }] } """));

        await fx.Manager.RunCommandAsync("acme.asyncy", "acme.asyncy.go", null, null);

        Assert.Equal("""{"boot":"booted","a":null,"b":"timer","sum":6}""", fx.Stored("acme.asyncy", "report"));
    }

    [Fact]
    public async Task Work_the_plugin_forgets_to_await_does_not_fault_the_engine()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(Plugin("sloppy", """
            export function activate(c) {
              void ncw.diagnostics.log('info', 'activating')
              c.subscriptions.push(ncw.commands.registerCommand('acme.sloppy.go', async () => {
                ncw.storage.global.set('fired', 'yes')        // 没 await
                setTimeout(() => {}, 30000)                    // 永远等不到的定时器
                ncw.storage.global.get('never-awaited')
                return 'returned early'
              }))
            }
            """, """ "permissions": ["storage"], "contributes": { "commands": [{ "command": "acme.sloppy.go", "title": "%a%" }] } """));

        await fx.Manager.RunCommandAsync("acme.sloppy", "acme.sloppy.go", null, null);
        await fx.Manager.RunCommandAsync("acme.sloppy", "acme.sloppy.go", null, null);

        Assert.Equal("yes", fx.Stored("acme.sloppy", "fired"));
        Assert.Equal(PluginStatus.Active, fx.Plugin("acme.sloppy").Status);
    }

    [Fact]
    public async Task An_unhandled_promise_rejection_is_a_diagnostic_not_a_crash()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(Plugin("rejecter", """
            export function activate(c) { c.subscriptions.push(ncw.commands.registerCommand('acme.rejecter.go', async () => { Promise.reject(new Error('nobody listens')); return 1 })) }
            """, """ "contributes": { "commands": [{ "command": "acme.rejecter.go", "title": "%a%" }] } """));

        await fx.Manager.RunCommandAsync("acme.rejecter", "acme.rejecter.go", null, null);

        Assert.Equal(PluginStatus.Active, fx.Plugin("acme.rejecter").Status);
        Assert.Contains(fx.Plugin("acme.rejecter").Diagnostics, d => d.Key == PluginDiagnosticKeys.UnhandledRejection && d.Message.Contains("nobody listens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_command_that_waits_on_the_user_longer_than_its_time_limit_fails_that_call_but_does_not_fault_the_plugin()
    {
        var fx = Fx(Small with { CommandTimeout = TimeSpan.FromMilliseconds(400) });
        await fx.InstallEnableAsync(Plugin("patient", """
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.patient.ask', async () => ncw.window.showConfirm({ titleKey: 'x' })))
              c.subscriptions.push(ncw.commands.registerCommand('acme.patient.ok', async () => 1))
            }
            """, """ "contributes": { "commands": [{ "command": "acme.patient.ask", "title": "%a%" }, { "command": "acme.patient.ok", "title": "%b%" }] } """));

        var e = await Assert.ThrowsAsync<PluginException>(() => fx.Manager.RunCommandAsync("acme.patient", "acme.patient.ask", null, null));

        Assert.Equal(PluginErrorKeys.CommandFailed, e.Key);
        Assert.Equal(PluginStatus.Active, fx.Plugin("acme.patient").Status); // 在等人,不是在空转
        await fx.Manager.RunCommandAsync("acme.patient", "acme.patient.ok", null, null);
        Assert.Empty(fx.Manager.Snapshot().Interactions); // 挂起的请求随调用结束被清掉
    }

    [Fact]
    public async Task Disabling_a_plugin_that_is_mid_call_interrupts_it_and_cancels_its_pending_prompt()
    {
        var fx = Fx();
        await fx.InstallEnableAsync(Plugin("midcall", """
            export function activate(c) { c.subscriptions.push(ncw.commands.registerCommand('acme.midcall.ask', async () => ncw.window.showConfirm({ titleKey: 'x' }))) }
            """, """ "contributes": { "commands": [{ "command": "acme.midcall.ask", "title": "%a%" }] } """));
        var running = fx.Manager.RunCommandAsync("acme.midcall", "acme.midcall.ask", null, null);
        await Wait.UntilAsync(() => fx.Manager.Snapshot().Interactions.Count == 1, "the pending prompt");

        await fx.Manager.SetEnabledAsync("acme.midcall", false);

        try
        {
            await running; // 提示被取消 = 回答「取消」,命令可以正常收尾;也可能先被中断:两种都可以,不许卡住
        }
        catch (PluginException)
        {
        }

        Assert.Empty(fx.Manager.Snapshot().Interactions);
        Assert.Equal(PluginStatus.Disabled, fx.Plugin("acme.midcall").Status);
    }

    [Fact]
    public async Task A_misbehaving_plugin_does_not_stop_the_manager_from_starting_the_others()
    {
        var fx = Fx();
        await fx.Manager.InstallZipAsync(PluginFx.FixtureZip("acme.think"));
        await fx.Manager.InstallZipAsync(Plugin("badstart2", "export function activate() { throw new Error('nope') }"));
        await fx.Manager.InstallZipAsync(Plugin("spinstart", "export function activate() { while (true) {} }"));
        foreach (var id in new[] { "acme.think", "acme.badstart2", "acme.spinstart" })
        {
            await fx.Manager.GrantAsync(id, []);
        }

        await fx.Manager.SetEnabledAsync("acme.think", true);
        await fx.Manager.SetEnabledAsync("acme.badstart2", true);
        await fx.Manager.SetEnabledAsync("acme.spinstart", true);

        var statuses = fx.Manager.Snapshot().Plugins.ToDictionary(p => p.Id, p => p.Status);
        Assert.Equal(PluginStatus.Active, statuses["acme.think"]);
        Assert.Equal(PluginStatus.Error, statuses["acme.badstart2"]);
        Assert.Equal(PluginStatus.Faulted, statuses["acme.spinstart"]);
    }
}

/// <summary>
/// 回归(「慢 / 坏拦截器」用例整程序集并发时失败的根因):宿主调用的计数按调用分账。
/// 上一次调用留下的、被掐断的宿主调用(还在睡的 <c>setTimeout</c>)会在取消回调里稍后才收尾,CPU 吃紧时这个「稍后」落在下一次调用开始之后;
/// 以前计数挂在宿主上,迟到的收尾把下一次调用里真正的死循环记成「在等宿主」,超时就不算故障。这里手动安排「迟到」,不靠时序。
/// </summary>
public sealed class CallAccountingTests
{
    [Fact]
    public void A_late_finish_of_a_previous_invocations_host_call_does_not_touch_the_next_invocation()
    {
        var accounting = new CallAccounting();
        accounting.BeginInvocation();
        var stale = accounting.Enter(); // 调用 1 里发起、被掐断但还没来得及收尾的宿主调用(比如 setTimeout 5000)
        Assert.Equal(1, accounting.InFlight);

        accounting.BeginInvocation(); // 调用 2 开始
        Assert.Equal((0, 0), (accounting.InFlight, accounting.Abandoned));

        CallAccounting.NoteAbandoned(stale); // 迟到的取消回调这时才跑
        CallAccounting.Exit(stale);

        Assert.Equal((0, 0), (accounting.InFlight, accounting.Abandoned)); // 调用 2 里的超时仍会被判成预算用尽(死循环),不是「在等宿主」
    }

    [Fact]
    public void Host_calls_of_the_current_invocation_are_counted_and_released_on_their_own_counters()
    {
        var accounting = new CallAccounting();
        accounting.BeginInvocation();
        var first = accounting.Enter();
        var second = accounting.Enter();
        Assert.Equal(2, accounting.InFlight);
        CallAccounting.NoteAbandoned(second);
        CallAccounting.Exit(second);
        Assert.Equal((1, 1), (accounting.InFlight, accounting.Abandoned));
        CallAccounting.Exit(first);
        Assert.Equal((0, 1), (accounting.InFlight, accounting.Abandoned));
    }
}
