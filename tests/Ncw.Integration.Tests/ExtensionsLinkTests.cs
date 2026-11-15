using System.IO.Compression;
using System.Text;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;

using static Ncw.Integration.Tests.Support.OrchestrationRig;

namespace Ncw.Integration.Tests;

/// <summary>
/// 扩展域在控制端的端到端:真实 Agent + 真实 <c>DeviceLink</c> / Store / <see cref="ExtensionActions"/>。
/// 验证「设备推送 → 归约 → Store」:另一个控制端改了配置,这个控制端靠 <c>extensions</c> 主题的修订号重拉自己持有的清单;MCP 状态是全量主题的镜像;
/// 断线是未知而不是旧值;Hook 的环境变量值、MCP 的密钥永远不进任何 Store。
/// </summary>
public sealed class ExtensionsLinkTests : IAsyncLifetime
{
    private readonly OrchestrationRig _rig = new();

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static byte[] SkillZip(string name)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry($"{name}/SKILL.md").Open(), new UTF8Encoding(false));
            writer.Write($"---\nname: {name}\ndescription: {name} description\n---\nbody of {name}\n");
        }

        return memory.ToArray();
    }

    private static T Read<T>(Controller c, Func<ClientStores, T> read)
    {
        lock (c.Ui.Gate)
        {
            return read(c.Stores);
        }
    }

    [Fact]
    public async Task A_skill_installed_by_another_controller_reaches_this_controllers_mirror_through_the_revision_topic()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var b = await _rig.StartControllerAsync("ctrl-b");
        var project = await a.Link.AddProjectAsync(_rig.Workspace);
        var deviceId = project.DeviceId;
        var extA = new ExtensionActions(a.Devices, a.Stores);
        var extB = new ExtensionActions(b.Devices, b.Stores);

        // A 持有这个项目的清单(空)
        Assert.True((await extA.Skills.ListAsync(deviceId, project.WorkspaceId)).IsOk);
        await WaitUntil(() => Read(a, s => s.Skills.State.Of(deviceId, project.WorkspaceId)) is { Known: true }, "A holds the list");
        Assert.Empty(Read(a, s => s.Skills.State.Of(deviceId, project.WorkspaceId))!.Skills);
        await WaitUntil(() => Read(a, s => s.ExtensionRevisions.State.Of(deviceId).Known), "A got the revisions snapshot");

        // B 装了一个技能:A 没做任何调用,清单自己追上
        var installed = await extB.Skills.InstallZipAsync(deviceId, SkillZip("shared-skill"), "shared-skill.zip", SkillScopes.Project, project.WorkspaceId);
        Assert.True(installed.IsOk, installed.Detail);
        await WaitUntil(() => Read(a, s => s.Skills.State.Of(deviceId, project.WorkspaceId))!.Skills.Any(k => k.Name == "shared-skill"), "A's list to catch up via the revision topic");

        // 命令同理:B 关掉 /init,A 持有的清单更新
        Assert.True((await extA.Commands.ListAllAsync(deviceId, project.WorkspaceId)).IsOk);
        Assert.True((await extB.Commands.SetEnabledAsync(deviceId, "init", false, project.WorkspaceId)).IsOk);
        await WaitUntil(() => Read(a, s => s.Commands.State.Of(deviceId, project.WorkspaceId))?.All.Any(c => c.Name == "init" && !c.Enabled) == true, "A's commands to catch up");

        // 修订号是单调的,key 带 deviceId
        var revisions = Read(a, s => s.ExtensionRevisions.State.Of(deviceId));
        Assert.True(revisions.Skills >= 1 && revisions.Commands >= 1);
    }

    [Fact]
    public async Task Mcp_servers_are_mirrored_from_the_full_topic_state_and_never_carry_secret_values()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var b = await _rig.StartControllerAsync("ctrl-b");
        var project = await a.Link.AddProjectAsync(_rig.Workspace);
        var deviceId = project.DeviceId;
        var extB = new ExtensionActions(b.Devices, b.Stores);
        const string secret = "sk-LINK-SECRET-424242";

        await WaitUntil(() => Read(a, s => s.Mcp.State.Of(deviceId).Known), "A got the mcp snapshot");
        var upsert = await extB.Mcp.UpsertAsync(deviceId, new McpServerConfigDto("off", "Off", McpTransports.Stdio, Enabled: false, Command: "sh", EnvNames: ["TOKEN"]));
        Assert.True(upsert.IsOk, upsert.Detail);
        Assert.True((await extB.Mcp.SetSecretsAsync(deviceId, "off", new Dictionary<string, string> { ["TOKEN"] = secret })).IsOk);

        await WaitUntil(() => Read(a, s => s.Mcp.State.Of(deviceId).Find("off")) is { State: "disabled" }, "A mirrors the disabled server");
        var mirror = Read(a, s => s.Mcp.State.Of(deviceId));
        Assert.True(mirror.Rev > 0);
        Assert.Equal(["TOKEN"], mirror.Find("off")!.Config.EnvNames!);   // 只有键名
        Assert.DoesNotContain(secret, mirror.ToString());

        var info = await extB.Mcp.GetSecretsInfoAsync(deviceId, "off");
        Assert.True(info.Value!.Entries.Single().IsSet);   // 回读只有「是否已设置」

        // 断线:MCP 状态未知,不把旧值当现状;重连后首帧带回来
        a.Current!.Cut();
        await WaitUntil(() => !Read(a, s => s.Mcp.State.Of(deviceId).Known), "the mcp mirror to turn unknown on disconnect");
        await WaitUntil(() => Read(a, s => s.Mcp.State.Of(deviceId)) is { Known: true } m && m.Find("off") is not null, "the mcp mirror to recover after the reconnect");
    }

    [Fact]
    public async Task Hook_env_values_are_written_one_way_and_the_mirror_only_ever_holds_names()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var project = await a.Link.AddProjectAsync(_rig.Workspace);
        var deviceId = project.DeviceId;
        var ext = new ExtensionActions(a.Devices, a.Stores);
        const string secret = "HOOK-LINK-SECRET-777";

        var saved = await ext.Hooks.SaveAsync(deviceId, "project", project.WorkspaceId,
            new HookUpsertDto(null, "PreToolUse", "command", "Bash", true, 5000, null, "true", null, null, null, [new HookEnvVarDto("API_KEY", secret)]));
        Assert.True(saved.IsOk, saved.Detail);

        await WaitUntil(() => Read(a, s => s.Hooks.State.Of(deviceId, project.WorkspaceId)) is { Known: true } l && l.Hooks.Length == 1, "the hook list in the mirror");
        var layer = Read(a, s => s.Hooks.State.Of(deviceId, project.WorkspaceId))!;
        Assert.Equal(["API_KEY"], layer.Hooks[0].EnvNames);
        Assert.DoesNotContain(secret, layer.ToString());
    }
}
