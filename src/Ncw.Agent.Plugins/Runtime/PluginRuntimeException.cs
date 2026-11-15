namespace Ncw.Agent.Plugins;

/// <summary>一次插件调用失败的分类(不含「调用方自己取消」:那是 <see cref="OperationCanceledException"/>,原样传播)。</summary>
internal enum PluginFailureKind
{
    /// <summary>插件自己 throw 了(引擎仍然可用)。</summary>
    Thrown,

    /// <summary>超出预算(超时 / 指令数 / 内存 / 栈深 / 输出):引擎已丢弃,插件进入 faulted。</summary>
    Budget,

    /// <summary>墙钟超时,但当时插件在等宿主(IO / 用户回答 / 定时器):不是它在空转,所以<b>不算故障</b>——这一次调用失败,引擎在下次调用前重建。</summary>
    Timeout,

    /// <summary>装载失败(找不到入口、编译失败、读不了源码)。</summary>
    Load,

    /// <summary>引擎自身故障(内部错误)。</summary>
    Engine,

    /// <summary>这个插件现在不在线(禁用 / 卸载 / faulted)。</summary>
    NotActive,

    /// <summary>引擎被上一个调用占着,等不到。</summary>
    Busy,
}

internal sealed class PluginRuntimeException(PluginFailureKind kind, string key, string message, string? reason = null, Exception? inner = null) : Exception(message, inner)
{
    public PluginFailureKind Kind { get; } = kind;

    /// <summary>稳定 key(<see cref="PluginDiagnosticKeys"/>)。</summary>
    public string Key { get; } = key;

    /// <summary>超预算时是哪一种:<c>timeout</c> / <c>instructions</c> / <c>memory</c> / <c>stack</c> / <c>output</c> / <c>jobs</c> / <c>modules</c>。</summary>
    public string? Reason { get; } = reason;
}
