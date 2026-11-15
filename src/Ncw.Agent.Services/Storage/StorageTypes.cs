using System.Security.Cryptography;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Storage;

public enum StorageFailure
{
    Invalid,
    NotFound,

    /// <summary>有运行中的 Agent / 会话操作 / 另一个备份正在进行:稍后重试。</summary>
    Busy,

    /// <summary>确认令牌缺失 / 过期 / 对不上这次操作。</summary>
    Unconfirmed,
}

/// <summary>存储维护的业务失败。<see cref="Key"/> 是稳定的 <c>storage.*</c> key(控制端翻译),<see cref="Kind"/> 决定协议错误码。</summary>
public sealed class StorageOperationException(StorageFailure kind, string key, string message) : Exception(message)
{
    public StorageFailure Kind { get; } = kind;

    public string Key { get; } = key;
}

/// <summary>设置 › 数据页的统计。附件字节是附件目录的<b>实际占用</b>(不是表里记的大小:表里可能有外部路径或已经丢失的文件);符号链接按链接自身计,绝不跟随到外部目录。</summary>
public sealed record StorageStats(
    long DbBytes,
    long WalBytes,
    long ConversationBytes,
    long AttachmentBytes,
    long ConversationCount,
    long MessageCount,
    long AttachmentCount,
    long UsageRecordCount,
    long WorkspaceCount,
    long? LastBackupAt);

public static class CleanupKinds
{
    public const string Attachments = "attachments";
    public const string Age = "age";
    public const string History = "history";
    public const string LocalData = "local-data";

    public static bool IsKnown(string kind) => kind is Attachments or Age or History or LocalData;

    /// <summary>需要二次确认的破坏性种类(会删对话 / 本机数据)。</summary>
    public static bool NeedsConfirmation(string kind) => kind is Age or History or LocalData;
}

/// <summary><c>7d</c> / <c>15d</c> 是天数;<c>1</c> / <c>3</c> / <c>6</c> / <c>12</c> 是日历月数(2.x 的 <c>CleanupAge</c>)。</summary>
public static class CleanupAges
{
    public static bool TryCutoff(long now, string? age, out long cutoff)
    {
        cutoff = 0;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        switch (age)
        {
            case "7d":
                cutoff = at.AddDays(-7).ToUnixTimeMilliseconds();
                return true;
            case "15d":
                cutoff = at.AddDays(-15).ToUnixTimeMilliseconds();
                return true;
            case "1" or "3" or "6" or "12":
                cutoff = at.AddMonths(-int.Parse(age, System.Globalization.CultureInfo.InvariantCulture)).ToUnixTimeMilliseconds();
                return true;
            default:
                return false;
        }
    }
}

/// <param name="ConfirmToken">破坏性种类的预览带一枚一次性确认令牌(<c>execute</c> 必须出示它:用户看过的数字就是实际发生的事);10 分钟过期。</param>
/// <param name="Undeletable">可能删不掉的路径(被占用 / 没有权限),界面提示用户手动处理。</param>
public sealed record CleanupPreview(string Kind, long SessionCount, long MessageCount, long AttachmentCount, long Bytes, IReadOnlyList<string> Undeletable, string? ConfirmToken = null);

public sealed record CleanupResult(CleanupPreview Preview, long Deleted);

/// <summary>
/// 二次确认令牌(危险操作的「确认参数」):预览时签发、执行时出示;绑定到<b>这一次操作</b>(种类 + 参数),一次性,10 分钟过期,常数时间比较。
/// 为什么不是一个 <c>confirm: true</c> 的布尔:布尔可以被任何调用方直接写死,令牌证明「这个调用方刚刚看过预览」。时钟可注入。
/// </summary>
public sealed class ConfirmTokens(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Operation, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);

    public string Issue(string operation)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in _tokens.Where(t => t.Value.Expires <= now).Select(t => t.Key).ToList())
            {
                _tokens.Remove(expired);
            }

            _tokens[token] = (operation, now + Lifetime);
        }

        return token;
    }

    /// <summary>出示令牌:对得上这次操作且没过期就消耗掉(一次性)并返回 true。</summary>
    public bool Redeem(string? token, string operation)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_gate)
        {
            var match = _tokens.FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(t.Key), System.Text.Encoding.UTF8.GetBytes(token)));
            if (match.Key is null || match.Value.Operation != operation || match.Value.Expires <= clock.GetUtcNow())
            {
                return false;
            }

            _tokens.Remove(match.Key);
            return true;
        }
    }
}

/// <summary>存储服务共用的环境:库、数据根、时钟、「有没有运行中的工作」(清理 / 恢复前必须没有)和备份目录(设置页的值,每次现读)。</summary>
public sealed record StorageEnvironment(
    AgentDatabase Database,
    string DataRoot,
    TimeProvider Clock,
    Func<bool> HasActiveWork,
    Func<string?> BackupDirectory,
    string AppVersion)
{
    public string AttachmentsRoot => Path.Combine(DataRoot, "attachments");

    public long Now => Clock.GetUtcNow().ToUnixTimeMilliseconds();
}
