using Ncw.Client.Devices;
using Ncw.Client.Stores;

namespace Ncw.Client.Actions;

/// <summary>
/// 扩展域(Skills / 命令 / Hooks / MCP)的全部用户意图,一处装配:四个领域的 Actions + 一个刷新器。
/// 刷新器订阅 <see cref="ClientStores.ExtensionRevisions"/>:某台设备上某个域的修订号前进了(别的控制端改了配置)、或设备重新连上(镜像之前被标成未知),
/// 就把这台设备上镜像里<b>持有的</b>那几份清单重拉一遍(没人看的清单不拉)。修订号只会前进(规则 6),所以重放的旧事件不会触发多余的重拉。
/// </summary>
public sealed class ExtensionActions
{
    private readonly ClientStores _stores;
    private readonly Dictionary<string, DeviceExtensionRevisions> _seen = new(StringComparer.Ordinal);

    public ExtensionActions(DeviceRegistry devices, ClientStores stores)
    {
        _stores = stores;
        Skills = new SkillsActions(devices, stores.Skills);
        Commands = new CommandsActions(devices, stores.Commands);
        Hooks = new HooksActions(devices, stores.Hooks);
        Mcp = new McpActions(devices);
        Plugins = new PluginsActions(devices, stores.Plugins);
        stores.ExtensionRevisions.Changed += OnRevisions;
    }

    public SkillsActions Skills { get; }

    public CommandsActions Commands { get; }

    public HooksActions Hooks { get; }

    public McpActions Mcp { get; }

    public PluginsActions Plugins { get; }

    /// <summary>刷新器处理完当前一批修订号变化(测试等「追平」用)。</summary>
    public Task Settled => _settled;

    private Task _settled = Task.CompletedTask;

    private void OnRevisions()
    {
        foreach (var (deviceId, now) in _stores.ExtensionRevisions.State.Devices)
        {
            if (!now.Known)
            {
                _seen.Remove(deviceId); // 断开后重新连上:首帧到来时全部重拉
                continue;
            }

            _seen.TryGetValue(deviceId, out var before);
            _seen[deviceId] = now;
            var skills = before is null || now.Skills > before.Skills;
            var commands = before is null || now.Commands > before.Commands;
            var hooks = before is null || now.Hooks > before.Hooks;
            if (!(skills || commands || hooks))
            {
                continue;
            }

            var id = deviceId;
            _settled = Task.WhenAll(_settled, RefreshAsync(id, skills, commands, hooks));
        }
    }

    private async Task RefreshAsync(string deviceId, bool skills, bool commands, bool hooks)
    {
        // 刷新失败(设备又断了 / 角色不够)不是错误:那份清单保持原样,下次修订号前进或重连后再来。
        if (skills)
        {
            await Skills.RefreshHeldAsync(deviceId).ConfigureAwait(false);
        }

        if (commands)
        {
            await Commands.RefreshHeldAsync(deviceId).ConfigureAwait(false);
        }

        if (hooks)
        {
            await Hooks.RefreshHeldAsync(deviceId).ConfigureAwait(false);
        }
    }
}
