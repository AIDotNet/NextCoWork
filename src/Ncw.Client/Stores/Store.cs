namespace Ncw.Client.Stores;

/// <summary>
/// 保存一个不可变状态值的 Store。每个运行时事实在控制端只有一份镜像,就放在某一个 Store 里(AGENTS.md §3 规则 1)。
/// 只在 UI 线程上读写;订阅者通过 <see cref="Changed"/> 得知变化后自己去读 <see cref="State"/>。
/// </summary>
public class Store<T>(T initial) where T : notnull
{
    public T State { get; private set; } = initial;

    public event Action? Changed;

    /// <summary>用一个纯函数更新状态。返回同一个实例时视为没有变化,不通知。</summary>
    public void Update(Func<T, T> change)
    {
        var next = change(State);
        if (ReferenceEquals(next, State) || EqualityComparer<T>.Default.Equals(next, State))
        {
            return;
        }

        State = next;
        Changed?.Invoke();
    }
}
