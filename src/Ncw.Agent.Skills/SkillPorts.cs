using Ncw.Agent.Environments;

namespace Ncw.Agent.Skills;

/// <summary>工作区里「选装了哪几条」:<see cref="Ids"/> 是白名单,<see cref="Explicit"/> = 用户手动选装过(此时空清单 = 一条都不要;否则空清单 = 全都要)。</summary>
public sealed record WorkspaceSkillSelection(IReadOnlyList<string> Ids, bool Explicit);

/// <summary>
/// Skill 的开关状态存放处(Host 用配置表 / 工作区设置实现;本层不认识 kv 与 SQLite)。
/// <list type="bullet">
/// <item><b>全局开关</b>存<b>被关掉的那些</b>(kv 键 <c>skills.disabled</c>,与 2.x 同):没被点过的一律有效,和「空清单 = 全都要」是同一个取向,新装的 Skill 默认可用。</item>
/// <item><b>工作区选装</b>是工作区设置里的 <c>activeSkillIds</c> + <c>skillSelectionMode</c>(<c>explicit</c> / 缺省),与 2.x 同。</item>
/// </list>
/// </summary>
public interface ISkillStateStore
{
    IReadOnlySet<string> DisabledGlobally();

    void SetGlobalEnabled(string skillId, bool enabled);

    /// <summary>工作区的选装状态;工作区不存在返回 null。</summary>
    WorkspaceSkillSelection? Selection(string workspaceId);

    void SetSelection(string workspaceId, IReadOnlyList<string> ids, bool explicitSelection);
}

/// <summary>某条 Skill 的用量(2.x 的 <c>skills.stats</c>)。<see cref="LastUsedAt"/> 是 Unix 毫秒,0 = 没用过。</summary>
public sealed record SkillUsage(int Count, long LastUsedAt);

/// <summary>「这条 Skill 被取用过一次」的出口。<b>永不阻断</b>取用:端口抛什么都被吞掉(一条 Skill 正文能不能取回来,和「记不记得住它被用过」是两件事)。</summary>
public interface ISkillUsage
{
    void Record(string skillId, string? workspaceId);

    /// <summary>指定工作区的用量(null = 全部工作区合计)。</summary>
    IReadOnlyDictionary<string, SkillUsage> Usage(string? workspaceId);
}

public enum SkillInstallScope
{
    Global,
    Project,
}

/// <summary>安装 / 卸载的落点:目录所在的执行环境 + 那个目录(全局 = 设备本机的 <c>&lt;数据根&gt;/skills</c>;项目 = 项目环境里的 <c>.next-cowork/skills</c>)。</summary>
public sealed record SkillInstallRoot(IExecutionEnvironment Environment, EnvPath Root);

/// <summary>
/// Host 实现的「Skill 在哪」:扫描输入(含插件层)和安装落点。项目环境暂时不可用(SSH 没连上)时应抛异常原样往上——那不是「没有 Skill」。
/// </summary>
public interface ISkillLocations
{
    ValueTask<SkillScanInput> ScanInputAsync(string? workspaceId, CancellationToken ct);

    /// <summary>项目作用域需要 <paramref name="workspaceId"/>;缺了抛 <see cref="SkillException"/>(<see cref="SkillErrorKeys.WorkspaceRequired"/>)。</summary>
    ValueTask<SkillInstallRoot> InstallRootAsync(SkillInstallScope scope, string? workspaceId, CancellationToken ct);
}

/// <summary>什么都不记的用量端口(测试和没装遥测的场合)。</summary>
public sealed class NullSkillUsage : ISkillUsage
{
    public static NullSkillUsage Instance { get; } = new();

    public void Record(string skillId, string? workspaceId)
    {
    }

    public IReadOnlyDictionary<string, SkillUsage> Usage(string? workspaceId) => new Dictionary<string, SkillUsage>();
}

/// <summary>内存里的开关状态(测试用)。</summary>
public sealed class MemorySkillStateStore : ISkillStateStore
{
    private readonly object _gate = new();
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceSkillSelection> _selections = new(StringComparer.Ordinal);

    public IReadOnlySet<string> DisabledGlobally()
    {
        lock (_gate)
        {
            return new HashSet<string>(_disabled, StringComparer.Ordinal);
        }
    }

    public void SetGlobalEnabled(string skillId, bool enabled)
    {
        lock (_gate)
        {
            if (enabled)
            {
                _disabled.Remove(skillId);
            }
            else
            {
                _disabled.Add(skillId);
            }
        }
    }

    public WorkspaceSkillSelection? Selection(string workspaceId)
    {
        lock (_gate)
        {
            return _selections.TryGetValue(workspaceId, out var s) ? s : new WorkspaceSkillSelection([], false);
        }
    }

    public void SetSelection(string workspaceId, IReadOnlyList<string> ids, bool explicitSelection)
    {
        lock (_gate)
        {
            _selections[workspaceId] = new WorkspaceSkillSelection([.. ids], explicitSelection);
        }
    }
}
