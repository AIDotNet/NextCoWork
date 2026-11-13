namespace Ncw.Client.Devices;

/// <summary>把协议回调切到 UI 线程。Store 只在 UI 线程上读写(AGENTS.md §3)。</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>测试和无界面场景:直接在当前线程执行。</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public static readonly InlineDispatcher Instance = new();

    public void Post(Action action) => action();
}
