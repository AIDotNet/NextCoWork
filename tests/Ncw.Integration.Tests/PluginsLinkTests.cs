using System.IO.Compression;
using System.Text;
using Ncw.Client.Actions;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;

using static Ncw.Integration.Tests.Support.OrchestrationRig;

namespace Ncw.Integration.Tests;

/// <summary>
/// 插件在控制端的端到端:真实 Agent(含 Jaunt 运行时)+ 真实 <c>DeviceLink</c> / Store / <see cref="PluginsActions"/>。
/// 「设备推送 → 归约 → Store」:另一个控制端装 / 启用 / 授权了插件,这个控制端靠 <c>plugins</c> 主题自己追上;变更方法的返回值和主题事件走同一个折叠函数;
/// 插件的提问(涉及执行的审批带设备 + 环境)进镜像、回答之后消失;断线是未知而不是旧值。
/// </summary>
public sealed class PluginsLinkTests : IAsyncLifetime
{
    private readonly OrchestrationRig _rig = new();

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static byte[] PluginZip()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry($"acme.linked/{path}").Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Add("package.json", """
                { "name": "linked", "publisher": "acme", "version": "1.0.0", "engines": { "nextcowork": "^0.3.0" }, "main": "./main.js",
                  "permissions": ["storage"], "optionalPermissions": ["process"], "allowedCommands": ["echo"],
                  "contributes": { "commands": [{ "command": "acme.linked.run", "title": "%run%" }] } }
                """);
            Add("main.js", """
                import * as ncw from 'nextcowork'
                export function activate(c) {
                  c.subscriptions.push(ncw.commands.registerCommand('acme.linked.run', async (args) => { await ncw.process.exec('echo', ['hi']) }))
                }
                """);
        }

        return memory.ToArray();
    }

    private static T Read<T>(Controller c, Func<Ncw.Client.Stores.ClientStores, T> read)
    {
        lock (c.Ui.Gate)
        {
            return read(c.Stores);
        }
    }

    [Fact]
    public async Task A_plugin_installed_and_enabled_by_another_controller_reaches_this_controllers_mirror_and_its_prompts_are_answerable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The command needs a POSIX shell environment.");
        var a = await _rig.StartControllerAsync("ctrl-a");
        var b = await _rig.StartControllerAsync("ctrl-b");
        var project = await a.Link.AddProjectAsync(_rig.Workspace);
        var deviceId = project.DeviceId;
        var extA = new ExtensionActions(a.Devices, a.Stores);
        var extB = new ExtensionActions(b.Devices, b.Stores);
        await WaitUntil(() => Read(a, s => s.Plugins.State.Of(deviceId).Known), "A got the plugins snapshot");
        Assert.Empty(Read(a, s => s.Plugins.State.Of(deviceId)).Plugins);

        // B 装、授权(必选 + 可选 process)、启用:A 没做任何调用,镜像自己追上
        var installed = await extB.Plugins.InstallPackageAsync(deviceId, PluginZip(), "linked.zip");
        Assert.True(installed.IsOk, installed.Detail);
        Assert.True((await extB.Plugins.GrantAsync(deviceId, "acme.linked", ["storage", "process"])).IsOk);
        var enabled = await extB.Plugins.SetEnabledAsync(deviceId, "acme.linked", true);
        Assert.True(enabled.IsOk, enabled.Detail);
        await WaitUntil(() => Read(a, s => s.Plugins.State.Of(deviceId).Find("acme.linked")) is { Status: PluginStatuses.Active }, "A's mirror to see the active plugin");
        var seen = Read(a, s => s.Plugins.State.Of(deviceId)).Find("acme.linked")!;
        Assert.Equal(["acme.linked.run"], seen.RegisteredCommands);
        Assert.Equal(["process", "storage"], seen.Granted.Order());
        // B 自己的镜像是同一份(变更方法的返回值和主题事件走同一个折叠函数)
        await WaitUntil(() => Read(b, s => s.Plugins.State.Of(deviceId).Find("acme.linked")) is { Status: PluginStatuses.Active }, "B's mirror");
        Assert.Equal(Read(a, s => s.Plugins.State.Of(deviceId).Rev), Read(b, s => s.Plugins.State.Of(deviceId).Rev));

        // 命令跑起来要执行 echo:提问进两边的镜像(带设备 + 环境);A 回答
        var running = extB.Plugins.RunCommandAsync(deviceId, "acme.linked", "acme.linked.run", project.WorkspaceId);
        await WaitUntil(() => Read(a, s => s.Plugins.State.Of(deviceId)).Interactions.Length == 1, "the exec prompt in A's mirror");
        var prompt = Read(a, s => s.Plugins.State.Of(deviceId)).Interactions[0];
        Assert.Equal((PluginInteractionKindNames.Exec, "echo hi"), (prompt.Kind, prompt.Command));
        Assert.Equal(deviceId, prompt.Environment!.DeviceId);
        await WaitUntil(() => Read(b, s => s.Plugins.State.Of(deviceId)).Interactions.Length == 1, "the exec prompt in B's mirror");
        Assert.True((await extA.Plugins.ReplyInteractionAsync(deviceId, prompt.RequestId, confirmed: true)).IsOk);
        Assert.True((await running).IsOk);
        await WaitUntil(() => Read(a, s => s.Plugins.State.Of(deviceId)).Interactions.Length == 0, "the prompt to vanish from the mirror");
        var activity = await extA.Plugins.ActivityAsync(deviceId, "acme.linked");
        Assert.Contains(activity.Value!.Entries, e => e.Method == "process.exec" && e.Verdict == "ok");

        // 失败是结构化的:卸载一个不存在的插件
        var gone = await extB.Plugins.UninstallAsync(deviceId, "acme.nobody");
        Assert.Equal((ActionOutcome.NotFound, "plugins.notFound"), (gone.Outcome, gone.ErrorKey));

        // 断线:插件状态未知,不把旧值当现状;重连后首帧带回来
        a.Current!.Cut();
        await WaitUntil(() => !Read(a, s => s.Plugins.State.Of(deviceId).Known), "the plugins mirror to turn unknown on disconnect");
        await WaitUntil(() => Read(a, s => s.Plugins.State.Of(deviceId)) is { Known: true } m && m.Find("acme.linked") is not null, "the plugins mirror to recover after the reconnect");
    }

    [Fact]
    public async Task Oversized_packages_are_refused_on_the_client_without_a_request()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var project = await a.Link.AddProjectAsync(_rig.Workspace);
        var ext = new ExtensionActions(a.Devices, a.Stores);

        var result = await ext.Plugins.InstallPackageAsync(project.DeviceId, new byte[PluginsLimits.MaxZipBytes + 1], "big.zip");

        Assert.Equal((ActionOutcome.Invalid, "plugins.packageTooLarge"), (result.Outcome, result.ErrorKey));
    }
}
