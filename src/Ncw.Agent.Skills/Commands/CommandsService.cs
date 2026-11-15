using Ncw.Agent.Kernel.Extensions;

namespace Ncw.Agent.Skills.Commands;

/// <summary>「哪些命令被用户关掉了」的存放处(2.x 的 <c>commands.disabled</c>;存<b>被关掉的那些</b>,每机器偏好,不写进文件——写进共享仓库会替队友做决定)。</summary>
public interface ICommandSwitchStore
{
    IReadOnlySet<string> Disabled();

    void SetEnabled(string name, bool enabled);
}

/// <summary>Host 实现的「命令在哪」:全局目录在设备本机,项目目录在项目的执行环境里。项目环境暂时不可用时抛异常原样往上。</summary>
public interface ICommandLocations
{
    ValueTask<CommandScanInput> ScanInputAsync(string? workspaceId, CancellationToken ct);
}

/// <summary>管理界面那张清单的一行(<see cref="Enabled"/> 标出被关掉的)。</summary>
public sealed record CommandListItem(string Name, string Description, CommandScope Scope, string Source, string? ArgumentHint, bool Enabled);

public sealed class MemoryCommandSwitchStore : ICommandSwitchStore
{
    private readonly object _gate = new();
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    public IReadOnlySet<string> Disabled()
    {
        lock (_gate)
        {
            return new HashSet<string>(_disabled, StringComparer.Ordinal);
        }
    }

    public void SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            if (enabled)
            {
                _disabled.Remove(name);
            }
            else
            {
                _disabled.Add(name);
            }
        }
    }
}

/// <summary>
/// 命令服务(2.x <c>ipc/commands.ts</c>)。★ <b>每次都重扫</b>,不返回缓存:用户刚往 <c>.next-cowork/commands/</c> 里放了一个文件就敲 <c>/</c>,而「关掉窗口再打开」不是他会想到要做的事。
/// <see cref="ListAsync"/> 是给<b>运行期</b>用的(已滤掉关掉的,带正文);<see cref="ListAllAsync"/> 给管理界面(关掉的也列出来,否则用户没法再打开它)。
/// 过滤放在这一层而不是扫描器里:「磁盘上有这条命令吗」和「我想不想用它」是两个问题。
/// </summary>
/// <param name="registered">一方扩展经 <c>RegisterCommand</c> 贡献的命令(Host 从会话扩展宿主收集);null = 没有。</param>
public sealed class CommandsService(ICommandLocations locations, ICommandSwitchStore switches, Func<IReadOnlyList<CommandDefinition>>? registered = null)
{
    /// <summary>启停变了(Host 据此把「扩展」主题的修订号加一)。</summary>
    public event Action? Changed;

    public async Task<CommandScanResult> ScanAsync(string? workspaceId, CancellationToken ct = default)
    {
        var input = await locations.ScanInputAsync(workspaceId, ct).ConfigureAwait(false);
        return await CommandLoader.ScanAsync(input with { Extension = registered?.Invoke() ?? [] }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SlashCommand>> ListAsync(string? workspaceId, CancellationToken ct = default)
    {
        var disabled = switches.Disabled();
        return [.. (await ScanAsync(workspaceId, ct).ConfigureAwait(false)).Commands.Where(c => !disabled.Contains(c.Name))];
    }

    public async Task<IReadOnlyList<CommandListItem>> ListAllAsync(string? workspaceId, CancellationToken ct = default)
    {
        var disabled = switches.Disabled();
        return [.. (await ScanAsync(workspaceId, ct).ConfigureAwait(false)).Commands.Select(c =>
            new CommandListItem(c.Name, c.Description, c.Scope, c.Source, c.ArgumentHint, !disabled.Contains(c.Name)))];
    }

    public async Task<IReadOnlyList<CommandDiagnostic>> DiagnosticsAsync(string? workspaceId, CancellationToken ct = default) =>
        (await ScanAsync(workspaceId, ct).ConfigureAwait(false)).Diagnostics;

    public void SetEnabled(string name, bool enabled)
    {
        switches.SetEnabled(name, enabled);
        Changed?.Invoke();
    }
}
