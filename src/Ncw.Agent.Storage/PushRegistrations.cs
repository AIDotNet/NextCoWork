using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ncw.Agent.Storage;

/// <summary>
/// 一个控制端登记的推送令牌。<see cref="ControllerId"/> 是控制端公钥指纹(小写十六进制);本机通道没有指纹,用 <see cref="PushRegistrations.LocalController"/>。
/// </summary>
public sealed record PushRegistration(string ControllerId, string Platform, string Token, string? AppVersion, long UpdatedAt);

/// <summary>
/// 推送令牌的登记表(kv 键 <c>v3.push.registrations</c>,整份 JSON;3.0 自己的键,不碰 2.x 的键)。
/// ★ 只存不发:发送是后端推送中继(docs/design/05 §6 B4)以后的事;这里保证的是「谁登记了什么」这一个事实只有一份,
/// 且被吊销的控制端不会再被当成发送目标(<see cref="ActiveFor"/>)。同一个控制端再次登记 = 覆盖;总数有上限,超出时淘汰最久没更新的。
/// </summary>
public sealed class PushRegistrations(AgentDatabase database)
{
    public const string Key = "v3.push.registrations";

    /// <summary>本机通道(桌面内置)登记时的控制端 id。桌面不接推送,这个值只是让协议在本机通道上也能自洽。</summary>
    public const string LocalController = "local";

    /// <summary>每台设备最多保留的令牌数(每个已配对控制端一个,远小于这个数;上限只是防御)。</summary>
    public const int MaxRegistrations = 64;

    private readonly KvStore _kv = new(database);
    private readonly object _gate = new();

    /// <summary>登记(覆盖)一个控制端的令牌,返回写下的记录。</summary>
    public PushRegistration Put(string controllerId, string platform, string token, string? appVersion, long now)
    {
        lock (_gate)
        {
            var list = Read().Where(r => r.ControllerId != controllerId).ToList();
            var entry = new PushRegistration(controllerId, platform, token, appVersion, now);
            list.Add(entry);
            if (list.Count > MaxRegistrations)
            {
                list = [.. list.OrderByDescending(r => r.UpdatedAt).Take(MaxRegistrations)];
            }

            Write(list);
            return entry;
        }
    }

    public PushRegistration? Get(string controllerId)
    {
        lock (_gate)
        {
            return Read().FirstOrDefault(r => r.ControllerId == controllerId);
        }
    }

    public IReadOnlyList<PushRegistration> List()
    {
        lock (_gate)
        {
            return Read();
        }
    }

    /// <summary>忘掉一个控制端的令牌(登出 / 吊销)。</summary>
    public bool Remove(string controllerId)
    {
        lock (_gate)
        {
            var list = Read();
            var kept = list.Where(r => r.ControllerId != controllerId).ToList();
            if (kept.Count == list.Count)
            {
                return false;
            }

            Write(kept);
            return true;
        }
    }

    /// <summary>
    /// 现在可以作为发送目标的令牌:只含仍然被信任的控制端(吊销之后它的令牌作废,不需要吊销流程另外通知这里)。
    /// 本机通道的令牌不是发送目标(桌面不接推送)。
    /// </summary>
    public IReadOnlyList<PushRegistration> ActiveFor(ControllerTrustRepository trust)
    {
        var trusted = trust.List().Select(t => t.Fingerprint).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            return [.. Read().Where(r => trusted.Contains(r.ControllerId))];
        }
    }

    private List<PushRegistration> Read()
    {
        if (_kv.Get(Key) is not { } json)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, PushJson.Default.PushRegistrationArray)?.ToList() ?? [];
        }
        catch (JsonException)
        {
            return []; // 坏了就当没有:控制端下次连接会重新登记
        }
    }

    private void Write(List<PushRegistration> list) =>
        _kv.Set(Key, JsonSerializer.Serialize(list.ToArray(), PushJson.Default.PushRegistrationArray));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PushRegistration[]))]
internal sealed partial class PushJson : JsonSerializerContext;
