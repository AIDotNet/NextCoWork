using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;

namespace Ncw.Agent.Extensions.Planning.Plans;

/// <summary>计划文件的固定约定(2.x <c>shared/domain/plan-file.ts</c>):工作区内 <c>.plan/&lt;planId&gt;.md</c>,整份读写都有上限但不妨碍写得详细。</summary>
public static class PlanFiles
{
    /// <summary>审批和执行时整份读文件的上限。</summary>
    public const long MaxBytes = 1024 * 1024;

    public static string PathOf(string planId) => $".plan/{planId}.md";

    public static string WireName(PlanApprovalAction action) => action switch
    {
        PlanApprovalAction.ApproveCurrent => "approve_current",
        PlanApprovalAction.ApproveNewSession => "approve_new_session",
        PlanApprovalAction.RequestRevision => "request_revision",
        _ => "reject",
    };
}

/// <summary>一份计划文件的引用:id 和工作区相对路径(恒为 <see cref="PlanFiles.PathOf"/>)。批准后由客户端带着它以 <c>code</c> 模式发起新 Run。</summary>
public sealed record PlanFileRef(string PlanId, string Path);

/// <summary>一个会话当前活动的计划。<see cref="Absolute"/> 是环境里的绝对路径(路径守卫解析过),写入围栏就围在它上面。</summary>
public sealed record ActivePlan(string PlanId, string Path, EnvPath Absolute, string SessionId);

/// <summary>
/// 活动计划,<b>按会话</b>存(2.x <c>plan-run.ts</c>)。
/// ★ 一次计划几乎总要跨好几轮:模型先问一批问题、用户回答,这中间 Run 早就结束了。按 RunId 存的话,下一个 Run 里计划就没了,
/// 工具允许列表又把 Write / Edit 摘掉——而模型还记得计划文件路径,照样去调。症状是一句「没有这个工具」,看起来像权限出了问题,其实是状态丢了。
/// <para>
/// 最多记住 <see cref="MaxScopes"/> 个会话的计划,超了丢最旧的:没有「会话结束」的钩子可挂。正常的清除在 <c>ExitPlanMode</c> 拿到批准 / 拒绝时发生,这里兜的是「计划提了一半就再也没回来」的那些。
/// 线程安全。
/// </para>
/// </summary>
public sealed class PlanRegistry
{
    public const int MaxScopes = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, (ActivePlan Plan, long Seq)> _plans = new(StringComparer.Ordinal);
    private long _seq;

    public ActivePlan? Get(string sessionId)
    {
        lock (_gate)
        {
            return _plans.TryGetValue(sessionId, out var entry) ? entry.Plan : null;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _plans.Count;
            }
        }
    }

    /// <summary>登记;会话已经有活动计划时失败并返回它(一个会话同时只有一份)。</summary>
    public bool TryBegin(ActivePlan plan, out ActivePlan? existing)
    {
        lock (_gate)
        {
            if (_plans.TryGetValue(plan.SessionId, out var current))
            {
                existing = current.Plan;
                return false;
            }

            // 超了丢插入最早的那个会话
            while (_plans.Count >= MaxScopes)
            {
                var oldest = _plans.MinBy(static kv => kv.Value.Seq).Key;
                _plans.Remove(oldest);
            }

            _plans[plan.SessionId] = (plan, ++_seq);
            existing = null;
            return true;
        }
    }

    public bool Leave(string sessionId)
    {
        lock (_gate)
        {
            return _plans.Remove(sessionId);
        }
    }
}

/// <summary>批准的计划无法载入(路径对不上 id、文件没了、是目录、空的、超过上限)。消息是英文诊断,给模型和日志看。</summary>
public sealed class PlanExecutionException(string message) : Exception(message);

/// <summary>批准后执行计划的 Run 要带上的内容(2.x <c>plan-execution.ts</c>)。</summary>
public sealed record PlanExecutionContext(string PlanId, string Path, string Content);

public static class PlanExecution
{
    /// <summary>
    /// 按引用载入已批准的计划:路径必须等于 <see cref="PlanFiles.PathOf"/>(客户端给的路径不可信),文件必须存在、是普通文件、非空、不超过上限、内容不是纯空白。
    /// 只经执行环境读文件;解析走路径守卫(<c>ResolveWithin</c>,逃出项目根就抛)。
    /// </summary>
    public static async ValueTask<PlanExecutionContext> LoadAsync(IExecutionEnvironment environment, EnvPath root, PlanFileRef reference, CancellationToken ct)
    {
        var expected = PlanFiles.PathOf(reference.PlanId);
        if (reference.Path != expected)
        {
            throw new PlanExecutionException("The approved plan path does not match its id.");
        }

        EnvPath absolute;
        FsEntryInfo stat;
        try
        {
            absolute = await environment.Paths.ResolveWithinAsync(root, expected, ct).ConfigureAwait(false);
            stat = await environment.Fs.StatAsync(absolute, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.NotFound)
        {
            throw new PlanExecutionException($"The approved plan file does not exist: {expected}");
        }

        if (stat.IsDirectory || stat.Size == 0 || stat.Size > PlanFiles.MaxBytes)
        {
            throw new PlanExecutionException($"The approved plan file is invalid: {expected}");
        }

        var content = await environment.Fs.ReadTextAsync(absolute, PlanFiles.MaxBytes, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new PlanExecutionException($"The approved plan file is empty: {expected}");
        }

        return new PlanExecutionContext(reference.PlanId, expected, content);
    }
}
