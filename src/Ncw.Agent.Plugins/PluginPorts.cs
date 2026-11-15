using Ncw.Agent.Environments;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 配置表(2.x 的 kv 表:<c>plugins.state</c>、<c>plugins.config.&lt;id&gt;</c>、<c>plugins.kv.&lt;id&gt;</c>——键和形状沿用 2.x,所以 2.x 数据被原地接管之后用户的启停与授权原样可用)。
/// 本层不认识 SQLite;Host 用 <c>KvStore</c> 实现。
/// </summary>
public interface IPluginKv
{
    string? Get(string key);

    void Set(string key, string json);
}

/// <summary>NCK1 凭据库的插件适配(<c>secrets.*</c>)。引用(<c>plugin:&lt;id&gt;:&lt;key&gt;</c>)由宿主拼,插件拼不了别人的前缀。</summary>
public interface IPluginSecrets
{
    string? Get(string reference);

    void Set(string reference, string value);

    bool Remove(string reference);
}

public sealed record PluginHttpRequest(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <param name="Body">已按上限截断的字节。</param>
public sealed record PluginHttpResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body, bool Truncated);

/// <summary>
/// <c>net.fetch</c> 的出口(可注入:测试用回环 / 假实现)。<b>一次只发一跳,不跟随重定向</b>:重定向由调用方逐跳重过一遍 URL 门(跨 host 的跳转被拒——跟着走等于让远端替插件改写 <c>hostPermissions</c>)。
/// 正文边读边数字节,到 <paramref name="maxBodyBytes"/> 就停(<see cref="PluginHttpResponse.Truncated"/>)。
/// </summary>
public interface IPluginHttp
{
    Task<PluginHttpResponse> SendAsync(PluginHttpRequest request, int maxBodyBytes, CancellationToken ct);
}

/// <summary>一个项目在设备上的落点:执行环境 + 根目录(环境里的绝对路径)。</summary>
public sealed record PluginWorkspaceTarget(string WorkspaceId, string Name, IExecutionEnvironment Environment, EnvPath Root);

/// <summary>项目解析(Host 用项目登记实现)。不存在返回 null。</summary>
public interface IPluginWorkspaces
{
    PluginWorkspaceTarget? Resolve(string workspaceId);
}

/// <summary>宿主给插件看的 App 信息(<c>env.appInfo</c>)。</summary>
public sealed record PluginAppInfo(string AppName, string AppVersion, string Language);

/// <summary>插件运行时的预算与限额(<see cref="JsLimits"/> 的上一层)。测试用小值验证「超预算被终止、不影响宿主」。</summary>
public sealed record PluginRuntimeLimits
{
    /// <summary>激活(模块求值 + <c>activate</c>)的墙钟上限。</summary>
    public TimeSpan ActivateTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>交互式工具(推带按钮的实时卡片、挂起等用户点)的硬上限:不信插件会自律,取消随时可用,但永不返回的工具最多钉住这一轮 5 分钟。</summary>
    public TimeSpan InteractiveToolTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>拦截器 / 上下文提供者的超时:超时 = 弃权(一个卡住的插件不该把所有工具调用堵死)。</summary>
    public TimeSpan HookTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>一次调用的虚拟机工作量上限(<see cref="JsLimits.MaxInstructions"/>:循环回边、调用、长内置的逐元素步骤)。</summary>
    public long MaxInstructionsPerCall { get; init; } = 500_000_000;

    /// <summary>引擎生命周期内累计的分配上限(<see cref="JsLimits.MaxTrackedBytes"/>,保守计数,不是 RSS)。超过 75% 时下一次调用前重建引擎(重新激活),单次调用越界 = faulted。</summary>
    public long MaxTrackedBytes { get; init; } = 256L * 1024 * 1024;

    public int MaxStackDepth { get; init; } = 2_000;

    /// <summary>JSON / 数据映射的输出上限(字节)。</summary>
    public long MaxOutputBytes { get; init; } = 16L * 1024 * 1024;

    public int MaxPendingJobs { get; init; } = 100_000;

    /// <summary>插件源码上限(字节)。</summary>
    public int MaxSourceBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>单插件 kv 配额(字节)。</summary>
    public int StorageQuotaBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>等用户回答一个插件交互的上限(到点按取消处理)。</summary>
    public TimeSpan InteractionTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>一次 <c>process.exec</c> 的超时(夹在 1 秒到这个值之间)。</summary>
    public TimeSpan MaxExecTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>读写文件 / 网络响应的单次字节上限。</summary>
    public int MaxFileBytes { get; init; } = 8 * 1024 * 1024;

    public int MaxLogChars { get; init; } = 2_000;
}
