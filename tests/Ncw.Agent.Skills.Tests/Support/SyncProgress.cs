namespace Ncw.Agent.Skills.Tests;

/// <summary>
/// 同步、按序、线程安全的 <see cref="IProgress{T}"/>。
/// <see cref="Progress{T}"/> 在没有同步上下文时把回调投到线程池,先后顺序和「是否已经跑完」都不保证,
/// 在负载高的整套并行测试里会乱序;测试要断言进度序列就必须用同步的实现。
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly List<T> _items = [];
    private readonly object _gate = new();

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            _items.Add(value);
        }
    }
}
