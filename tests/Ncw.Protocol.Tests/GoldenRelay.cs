namespace Ncw.Protocol.Tests;

/// <summary>
/// 中继承载(09 §9)的冻结向量。中继的控制消息是 JSON 文本(不是帧),设备 / 控制端 / 中继三方共用 <c>RelayMessage</c> 一份定义;
/// 键名、省略 null、字段顺序都是线上格式的一部分,改了必须升 <c>RelayProtocol.Version</c> 或加能力开关,不能直接改向量。
/// 另外冻结的是 M6 追加的「带默认值的末尾字段」:remote.configure 的 <c>RelayUrl</c>、remote 状态的中继三项、
/// 配对结果和 device.info 的 <c>Relay</c>——旧载荷(没有这些键)必须照常解码(见 <see cref="RelayWireTests"/>),新载荷的字节也冻结在这里。
/// </summary>
internal static class GoldenRelay
{
    // ---- 中继控制消息(JSON 文本)-------------------------------------------------------------------------------

    public const string Challenge = "{\"type\":\"challenge\",\"v\":1,\"nonce\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"}";
    public const string Register = "{\"type\":\"register\",\"v\":1,\"publicKey\":\"cHVibGlj\",\"signature\":\"c2ln\",\"tokens\":[\"aa\",\"bb\"]}";
    public const string Registered = "{\"type\":\"registered\",\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\"}";
    public const string TokensSet = "{\"type\":\"tokens\",\"set\":[\"aa\"]}";
    public const string TokensDelta = "{\"type\":\"tokens\",\"add\":[\"aa\"],\"remove\":[\"bb\"]}";
    public const string Session = "{\"type\":\"session\",\"session\":\"0123456789abcdef0123456789abcdef\",\"tokenHash\":\"aa\"}";
    public const string Connect = "{\"type\":\"connect\",\"v\":1,\"token\":\"dG9rZW4=\"}";
    public const string Ready = "{\"type\":\"ready\"}";
    public const string Error = "{\"type\":\"error\",\"code\":\"device_offline\",\"message\":\"gone\"}";

    // ---- 追加的末尾字段(MessagePack,小写十六进制)-------------------------------------------------------------

    public const string PairFinishResultWithRelay =
        "85a24d32c4020102a550726f6f66c40103a4526f6c65a66d656d626572aa526f757465546f6b656ea3746f6ba552656c6179b37773733a2f2f72656c61792e6578616d706c65";
    public const string PairClaimResultWithRelay =
        "83a4526f6c65a66d656d626572aa526f757465546f6b656ea3746f6ba552656c6179b37773733a2f2f72656c61792e6578616d706c65";
    public const string DeviceInfoWithRelay =
        "87a84465766963654964a164a44e616d65a16ea44b696e64a56c6f63616cac4167656e7456657273696f6ea5332e302e30a24f73a26f73a844617461526f6f74a22f72a552656c6179b37773733a2f2f72656c61792e6578616d706c65";
    public const string RemoteStatusWithRelay =
        "8aa7456e61626c6564c3a4506f7274cd1e82ad507265666572726564506f7274cd1e82a74c616e4f6e6c79c3a941646472657373657391ab3139322e3136382e312e32ab50616972696e67436f6465a6313233343536a54572726f72c0a852656c617955726cb37773733a2f2f72656c61792e6578616d706c65aa52656c61795374617465aa72656769737465726564aa52656c61794572726f72c0";
    public const string RemoteConfigureWithRelay =
        "83a4506f7274c0a74c616e4f6e6c79c0a852656c617955726cb37773733a2f2f72656c61792e6578616d706c65";
    public const string RemoteConfigureClearRelay =
        "83a4506f7274c0a74c616e4f6e6c79c0a852656c617955726ca0";
}
