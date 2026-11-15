using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Cloud;

/// <summary>云端状态的持久化接缝(Host 用数据库 kv 表实现;测试用内存实现)。值是 JSON 文本或十进制数字。读写必须是原子的。</summary>
public interface ICloudStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);
}

/// <summary>云端状态在 kv 表里的键(都以 <c>v3.cloud.</c> 开头,不碰 2.x 和其它 3.0 键)。</summary>
public static class CloudKeys
{
    public const string CommandSeq = "v3.cloud.cmdSeq";
    public const string Quota = "v3.cloud.quota";
    public const string Ledger = "v3.cloud.ledger";
    public const string Report = "v3.cloud.report";
    public const string Sleep = "v3.cloud.sleep";
    public const string Interrupted = "v3.cloud.interrupted";
    public const string Binding = "v3.cloud.binding";
    public const string Registered = "v3.cloud.registered";
}

/// <summary>一个被休眠 / 升级打断的 Run(持久化形式,和 <c>CloudInterruptedRunDto</c> 一一对应)。</summary>
public sealed record InterruptedRun(string RunId, string SessionId, long At);

/// <summary>休眠快照:休眠前写下,唤醒后读出并清掉(<see cref="CloudKeys.Sleep"/>)。唤醒时有它 = 「从休眠中醒来」。</summary>
public sealed record SleepSnapshot(long At, string Reason, SleepSummary Summary, long? WakeAt);

public sealed record InterruptedRuns(InterruptedRun[] Runs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QuotaLimits))]
[JsonSerializable(typeof(UsageLedger))]
[JsonSerializable(typeof(ReportState))]
[JsonSerializable(typeof(SleepSnapshot))]
[JsonSerializable(typeof(InterruptedRuns))]
[JsonSerializable(typeof(DeviceBinding))]
public sealed partial class CloudStateJson : JsonSerializerContext;

public static class CloudStoreExtensions
{
    public static T? Read<T>(this ICloudStore store, string key, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        if (store.Get(key) is not { } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, info);
        }
        catch (JsonException)
        {
            return null; // 损坏的状态当作没有:宁可重新计数,也不要因此起不来
        }
    }

    public static void Write<T>(this ICloudStore store, string key, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        store.Set(key, JsonSerializer.Serialize(value, info));

    public static long ReadLong(this ICloudStore store, string key) =>
        store.Get(key) is { } text && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    public static void WriteLong(this ICloudStore store, string key, long value) =>
        store.Set(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>账户身份令牌的当前值(编排通道每次现取;编排服务经 <c>account.token</c> 指令轮换)。令牌是秘密:不进日志、不进 <c>ToString</c>。</summary>
public sealed class AccountTokenHolder(string? initial)
{
    private string? _current = initial;

    public string? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public override string ToString() => "AccountTokenHolder(***)";
}
