using Ncw.Client.Stores;

namespace Ncw.Client.Updates;

/// <summary>引擎给出的一份可安装的更新:验过签名的清单 + 引擎自己的句柄(Velopack 的 <c>UpdateInfo</c> 等,服务不看里面)。</summary>
public sealed record UpdateOffer(string Version, ReleaseManifest Manifest, object Handle);

/// <param name="Manifest">验过签名的清单(哪怕没有更新也返回:上次检查时间和策略都来自它)。</param>
/// <param name="Offer">有更新可装时非 null。引擎认为没有更新(版本相同或 feed 里的更低)时为 null。</param>
public sealed record UpdateCheckResult(ReleaseManifest Manifest, UpdateOffer? Offer);

/// <summary>
/// 更新引擎:平台相关的那一半(桌面端用 Velopack 的 <c>UpdateManager</c> + 验签的 feed 源包装,在 <c>Ncw.Desktop/Updates/</c>)。
/// Client 里的 <see cref="UpdateService"/> 是状态机,只依赖这个接口,所以可以用假引擎测试。
/// <b>引擎保证:没验过清单签名的东西不会被下载</b>(验签发生在读 feed 的那一刻,不是服务调用的)。
/// </summary>
public interface IUpdateEngine
{
    /// <summary>是不是「装出来」的应用(能自更新)。开发时 <c>dotnet run</c> 不是。</summary>
    bool IsInstalled { get; }

    /// <summary>当前运行的版本(安装包里的版本)。</summary>
    string CurrentVersion { get; }

    /// <summary>读 feed、验清单、问更新器有没有新版本。失败抛 <see cref="UpdateException"/>。</summary>
    Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct);

    /// <summary>下载并校验(sha256 与清单一致)。<paramref name="progress"/> 是 0–100。失败抛 <see cref="UpdateException"/>。</summary>
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct);

    /// <summary>告诉更新器「等本进程退出之后应用更新并重启应用」。<b>不退出进程</b>(退出由宿主走自己的流程)。失败抛 <see cref="UpdateException"/>。</summary>
    void StageRestart(UpdateOffer offer);
}

/// <summary>
/// 更新重启时宿主要做的两件事:先把后台服务收尾(桌面端:让 ncw-agent 优雅退出),再走应用自己的退出流程。
/// 顺序由 <see cref="UpdateService"/> 保证:先让更新器就位(失败时应用还完整可用),再收尾,最后退出。
/// </summary>
public interface IUpdateHost
{
    /// <summary>收尾后台服务(sidecar)。不能抛;超时由实现自己负责。</summary>
    Task PrepareForRestartAsync(CancellationToken ct);

    /// <summary>发起应用退出(走正常的退出流程;更新器在进程退出后接手)。</summary>
    void RequestExit();
}

/// <summary>没有宿主时(测试、预览):什么也不做。</summary>
public sealed class NoUpdateHost : IUpdateHost
{
    public static readonly NoUpdateHost Instance = new();

    public Task PrepareForRestartAsync(CancellationToken ct) => Task.CompletedTask;

    public void RequestExit()
    {
    }
}

/// <summary>跨会话保留通道和「上次检查时间」。</summary>
public interface IUpdateMetaStore
{
    UpdateMeta? Load();

    void Save(UpdateMeta meta);
}

public sealed class JsonFileUpdateMetaStore(string path) : IUpdateMetaStore
{
    public UpdateMeta? Load()
    {
        try
        {
            return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJson.Default.UpdateMeta) : null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(UpdateMeta meta)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(meta, UpdateJson.Default.UpdateMeta));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 记录写不进去不影响更新本身
        }
    }
}

/// <summary>内存实现(测试)。</summary>
public sealed class MemoryUpdateMetaStore : IUpdateMetaStore
{
    public UpdateMeta? Stored { get; set; }

    public UpdateMeta? Load() => Stored;

    public void Save(UpdateMeta meta) => Stored = meta;
}

/// <summary>
/// 自更新服务。界面和宿主都通过它(经 <see cref="Actions.UpdateActions"/>);状态写进 <see cref="ClientStores.Updates"/>。
/// 同一个用户意图只有一个方法(规则 4):「检查」「下载」「重启并更新」「换通道」。
/// </summary>
public interface IUpdateService : IAsyncDisposable
{
    /// <summary>读本地记录、决定是否启用(不是安装出来的应用就是 <see cref="UpdatePhase.Disabled"/>),并开始后台定时检查。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>检查更新。已经有检查 / 下载在进行时返回同一个任务;已经下载好(Ready)时什么也不做。</summary>
    Task CheckAsync(CancellationToken ct = default);

    /// <summary>下载并校验已发现的更新。没有 Available 的更新时什么也不做。</summary>
    Task DownloadAsync(CancellationToken ct = default);

    /// <summary>应用已下载的更新并重启。返回 true = 更新器已就位、应用正在退出;false = 没有可装的更新或交接失败(状态里有原因,应用仍然可用)。</summary>
    Task<bool> RestartAndApplyAsync(CancellationToken ct = default);

    /// <summary>换通道(下载中 / 已下载待装 / 正在安装时不允许)。换成功后立刻检查一次。返回是否已换。</summary>
    Task<bool> SetChannelAsync(UpdateChannel channel, CancellationToken ct = default);
}
