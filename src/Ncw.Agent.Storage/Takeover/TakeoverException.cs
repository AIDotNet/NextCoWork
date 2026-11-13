namespace Ncw.Agent.Storage.Takeover;

/// <summary>接管失败的原因。结构化给出,界面文字由控制端按原因生成。</summary>
public enum TakeoverFailure
{
    /// <summary>2.x 桌面端还在用这份数据(Chromium 的 SingletonLock 指向活进程)。</summary>
    LegacyAppRunning,

    /// <summary>2.x 的独立设备服务还在用这份数据(service.lock)。</summary>
    LegacyServiceRunning,

    /// <summary>判断不了 2.x 是否在运行(锁文件读不了)。按「在运行」处理,不抢。</summary>
    OwnerUnknown,

    /// <summary>数据根里没有 2.x 的数据库。</summary>
    NoDatabase,

    /// <summary>旧布局(扁平布局或 Electron 默认目录),需要先用 2.x 最新版打开一次整理布局。</summary>
    UnsupportedLayout,

    /// <summary>已经接管过 / 没有待提交的接管 / 被另一次接管占用。</summary>
    Conflict,

    /// <summary>提交后回退会丢掉接管后写入的数据,必须显式确认。</summary>
    DataLossNotAcknowledged,

    /// <summary>没有可以回退的接管,或备份不见了。</summary>
    NothingToRollBack,

    /// <summary>备份没通过完整性检查或行数核对。</summary>
    BackupInvalid,

    /// <summary>接管日志损坏,拒绝继续。</summary>
    JournalCorrupt,
}

public sealed class TakeoverException(TakeoverFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public TakeoverFailure Failure { get; } = failure;
}
