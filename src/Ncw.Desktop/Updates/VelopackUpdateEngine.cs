using Ncw.Client.Updates;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace Ncw.Desktop.Updates;

/// <summary>
/// <see cref="IUpdateEngine"/> 的 Velopack 实现(docs/design/04 §3):<c>UpdateManager</c> + <see cref="SignedFeedSource"/>。
/// 每次检查按通道新建一个 <c>UpdateManager</c>(通道在构造选项里);验签、sha256、「只放行清单里的包」都在 <see cref="SignedFeedSource"/> 里,
/// 这里只负责把 Velopack 的结果和异常翻译成服务层的语言。<b>不降级</b>:<c>AllowVersionDowngrade = false</c>。
/// <paramref name="locator"/> 为 null 时用 Velopack 的默认定位(装出来的应用);测试传 <see cref="TestVelopackLocator"/>。
/// </summary>
public sealed class VelopackUpdateEngine : IUpdateEngine, IDisposable
{
    private sealed record Handle(UpdateManager Manager, UpdateInfo Info);

    private readonly UpdateConfiguration _config;
    private readonly IVelopackLocator? _locator;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action<string> _log;
    private readonly Lazy<UpdateManager> _probe;

    public VelopackUpdateEngine(UpdateConfiguration config, IVelopackLocator? locator = null, HttpClient? http = null, Action<string>? log = null)
    {
        _config = config;
        _locator = locator;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _log = log ?? (_ => { });
        // 只用来读「是不是装出来的」「当前版本」:数据源随便给一个不会被访问的目录
        _probe = new Lazy<UpdateManager>(() => new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.GetTempPath())), null, locator));
    }

    /// <summary>
    /// 是不是 Velopack 装出来的应用。<c>VelopackApp.Run()</c> 没跑过(没有定位器)也当作「不是」:任何在启动钩子之前 / 之外构造引擎的场合都不抛。
    /// </summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                return _probe.Value.IsInstalled;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public string CurrentVersion
    {
        get
        {
            try
            {
                return _probe.Value.CurrentVersion?.ToNormalizedString() ?? "0.0.0";
            }
            catch (InvalidOperationException)
            {
                return "0.0.0";
            }
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        var (source, _) = CreateSource(channel);
        var manager = new UpdateManager(source, new Velopack.UpdateOptions { ExplicitChannel = UpdateChannels.Id(channel), AllowVersionDowngrade = false }, _locator);
        UpdateInfo? info;
        try
        {
            info = await manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Translate(e, UpdateFailureKind.Network);
        }

        var manifest = source.LastManifest
            ?? throw new UpdateException(UpdateFailureKind.FeedInvalid, "the update source did not produce a verified manifest");
        _log($"update: feed {Describe(channel)} announces {manifest.Version} (running {CurrentVersion}); updater says {(info is null ? "nothing to do" : info.TargetFullRelease.Version)}");
        return new UpdateCheckResult(manifest, info is null ? null : new UpdateOffer(info.TargetFullRelease.Version.ToNormalizedString(), manifest, new Handle(manager, info)));
    }

    public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
    {
        var handle = (Handle)offer.Handle;
        try
        {
            await handle.Manager.DownloadUpdatesAsync(handle.Info, progress, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Translate(e, UpdateFailureKind.DownloadFailed);
        }
    }

    public void StageRestart(UpdateOffer offer)
    {
        var handle = (Handle)offer.Handle;
        try
        {
            // 更新器等本进程退出,再替换文件并重新拉起应用;这里不退出,退出由宿主走自己的流程(先收尾 sidecar)。
            handle.Manager.WaitExitThenApplyUpdates(handle.Info.TargetFullRelease, silent: true, restart: true, restartArgs: []);
        }
        catch (Exception e)
        {
            throw Translate(e, UpdateFailureKind.ApplyFailed);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private (SignedFeedSource Source, FeedLocation Location) CreateSource(UpdateChannel channel)
    {
        var location = FeedLocation.Resolve(_config.FeedTemplate, channel, _config.Rid)
            ?? throw new UpdateException(UpdateFailureKind.FeedInvalid, $"the update feed '{_config.FeedTemplate}' is not an https:// address, a loopback http:// address or a local directory");
        IUpdateSource inner;
        IFeedReader reader;
        if (location.Http is { } uri)
        {
            inner = new SimpleWebSource(uri, null, 30);
            reader = new HttpFeedReader(_http, uri);
        }
        else
        {
            inner = new SimpleFileSource(new DirectoryInfo(location.Directory!));
            reader = new DirectoryFeedReader(location.Directory!);
        }

        return (new SignedFeedSource(inner, reader, _config.Keys, channel, _config.Rid), location);
    }

    private string Describe(UpdateChannel channel) => FeedLocation.Resolve(_config.FeedTemplate, channel, _config.Rid)?.Describe ?? _config.FeedTemplate;

    /// <summary>Velopack 抛的异常(可能包在别的异常里)→ <see cref="UpdateException"/>。我们自己抛的原样穿过;校验和失败、没有安装、文件找不到各归各的类。</summary>
    private static UpdateException Translate(Exception e, UpdateFailureKind fallback)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case UpdateException update:
                    return update;
                case ChecksumFailedException:
                    return new UpdateException(UpdateFailureKind.ChecksumMismatch, inner.Message, e);
                case NotInstalledException:
                    return new UpdateException(UpdateFailureKind.ApplyFailed, "the app is not installed by Velopack", e);
            }
        }

        return e switch
        {
            FileNotFoundException or DirectoryNotFoundException => new UpdateException(UpdateFailureKind.FeedInvalid, e.Message, e),
            _ => new UpdateException(fallback, e.Message, e),
        };
    }
}
