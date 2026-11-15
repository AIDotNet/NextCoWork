using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;

namespace Ncw.Agent.Tools;

/// <summary>
/// 「这次运行里读过哪些文件」—— Write / Edit 的前置条件(移植自 2.x <c>read-tracker.ts</c>)。
/// 没 Read 过的已有文件不许写:防的是模型凭记忆整体覆盖一个文件、把没看见的内容一起抹掉。
/// 状态按 <see cref="RunId"/> 分桶:新的 Run 应当重新确认文件的当下内容,而不是继承上一个 Run 几分钟前读到的那一版。
/// 最多记住 <see cref="MaxRuns"/> 个 Run,超了丢最旧的(没有可靠的 Run 结束钩子,靠上限封顶)。
/// </summary>
public sealed class ReadTracker
{
    public const int MaxRuns = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _byRun = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public void MarkRead(RunId run, EnvPath path)
    {
        lock (_gate)
        {
            if (!_byRun.TryGetValue(run.Value, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                while (_byRun.Count >= MaxRuns && _order.TryDequeue(out var oldest))
                {
                    _byRun.Remove(oldest);
                }

                _byRun[run.Value] = set;
                _order.Enqueue(run.Value);
            }

            set.Add(Key(path));
        }
    }

    public bool WasRead(RunId run, EnvPath path)
    {
        lock (_gate)
        {
            return _byRun.TryGetValue(run.Value, out var set) && set.Contains(Key(path));
        }
    }

    /// <summary>Windows 风格的路径大小写不敏感:折叠后再当 key。</summary>
    internal static string Key(EnvPath path) => path.Style == PathStyle.Windows ? path.Value.ToUpperInvariant() : path.Value;
}
