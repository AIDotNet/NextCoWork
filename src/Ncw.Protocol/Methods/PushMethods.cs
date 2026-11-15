using PolyType;

namespace Ncw.Protocol.Methods;

// 推送令牌登记(docs/design/02 A4 / A5.3、05 §6 B4、09 §8.4 的 `push.register`)。
// ★ 设备只「存」令牌,不「发」推送:发送由 nextco.work 的推送中继(B4,M5 之后的后端工作)完成,中继只转发不透明的事件 id,不带会话内容。
// ★ 令牌属于「设备 + 控制端」:同一个控制端(按它的公钥指纹)再次登记就是覆盖;控制端被吊销后它的令牌不再参与发送(见 PushRegistrations.ActiveFor)。

/// <summary>推送平台。</summary>
public static class PushPlatforms
{
    /// <summary>Apple 推送(iOS / iPadOS)。</summary>
    public const string Apns = "apns";

    /// <summary>Firebase Cloud Messaging(Android)。</summary>
    public const string Fcm = "fcm";

    public static bool IsKnown(string? platform) => platform is Apns or Fcm;
}

/// <summary><c>push.register</c>:登记(覆盖)本控制端在这台设备上的推送令牌。</summary>
/// <param name="Platform"><see cref="PushPlatforms"/> 之一。</param>
/// <param name="Token">平台给的设备令牌(不透明字符串;长度 ≤ 4096)。</param>
/// <param name="AppVersion">控制端 App 的版本,给中继做兼容判断用;可空。</param>
public sealed record PushRegisterParams(string Platform, string Token, string? AppVersion = null);

/// <param name="Platform">已登记的平台。</param>
/// <param name="RegisteredAt">设备记下的登记时间(Unix 毫秒)。</param>
public sealed record PushRegisterResult(string Platform, long RegisteredAt);

/// <summary>推送方法表(能力 <see cref="Capabilities.Push"/>,承载 本机 + 远程):member 起。</summary>
public static class PushMethods
{
    public const int MaxTokenLength = 4096;

    public static readonly MethodDef<PushRegisterParams, PushRegisterResult> Register =
        MethodTable.Command<PushRegisterParams, PushRegisterResult>("push.register", Role.Member, Capabilities.Push);

    public static readonly IReadOnlyList<MethodDef> All = [Register];
}

public static partial class Capabilities
{
    /// <summary>推送令牌登记:<c>push.register</c>(member 起;设备只存不发,发送由后端推送中继负责)。</summary>
    public const string Push = "push";
}

[GenerateShapeFor<PushRegisterParams>]
[GenerateShapeFor<PushRegisterResult>]
public sealed partial class PayloadShapes;
