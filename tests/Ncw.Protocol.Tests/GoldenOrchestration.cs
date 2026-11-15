namespace Ncw.Protocol.Tests;

/// <summary>
/// 编排控制通道(Agent ↔ 编排服务)的冻结向量(<c>OrchestrationProtocol.Version = 1</c>)。通道是 JSON 文本:键名、省略 null、字段顺序都是线上格式的一部分,
/// 改了必须升 <c>OrchestrationProtocol.Version</c> 或加可选字段(旧载荷照常解码),不能直接改向量。Ed25519 签名是确定性的(RFC 8032),
/// <see cref="SignatureBase64"/> 是「种子 = 0..31、域前缀 <c>ncw-orch-command/1</c>、载荷 = <c>command.trust.put</c> 那段 JSON」的签名,后端用任何标准实现都能复现。
/// </summary>
internal static class GoldenOrchestration
{
    public const string SignatureBase64 =
        "69H/EUjkqcOsarEd9hfKCDHi5FzvRL6eQ51NKH+gZf7vn1OUQeJjEGVd4zdFUqT9GRdQ1aiGK/JHSslcraCOCw==";

    public const string PublicKeyBase64 = "A6EHv/POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg=";

    public static string Of(string name) => name switch
    {
        "command.trust.put" =>
            "{\"type\":\"trust.put\",\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"seq\":7,\"issuedAt\":1700000000000,\"expiresAt\":1700000600000,\"trust\":{\"fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"iPhone\",\"role\":\"owner\",\"routeTokenHash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}}",
        "command.trust.revoke" =>
            "{\"type\":\"trust.revoke\",\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"seq\":7,\"issuedAt\":1700000000000,\"expiresAt\":1700000600000,\"fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}",
        "command.trust.replace" =>
            "{\"type\":\"trust.replace\",\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"seq\":7,\"issuedAt\":1700000000000,\"expiresAt\":1700000600000,\"trusts\":[{\"fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"iPhone\",\"role\":\"owner\",\"routeTokenHash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}]}",
        "command.quota.set" =>
            "{\"type\":\"quota.set\",\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"seq\":7,\"issuedAt\":1700000000000,\"expiresAt\":1700000600000,\"quota\":{\"dailyRuns\":50,\"monthlyTokens\":5000000,\"maxConcurrentRuns\":2,\"frozen\":false}}",
        "command.sleep.request" =>
            "{\"type\":\"sleep.request\",\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"seq\":7,\"issuedAt\":1700000000000,\"expiresAt\":1700000600000,\"sleep\":{\"mode\":\"whenIdle\",\"reason\":\"scale-down\"}}",
        "command.upgrade.request" =>
            "{\"type\":\"upgrade.request\",\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"seq\":7,\"issuedAt\":1700000000000,\"expiresAt\":1700000600000,\"upgrade\":{\"version\":\"3.0.1\",\"mode\":\"whenIdle\"}}",
        "command.account.token" =>
            "{\"type\":\"account.token\",\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"seq\":7,\"issuedAt\":1700000000000,\"expiresAt\":1700000600000,\"account\":{\"token\":\"example-token\",\"expiresAt\":1700003600000,\"accountId\":\"acct-1\",\"teamId\":\"team-1\"}}",
        "envelope" =>
            "{\"v\":1,\"kid\":\"orch-1\",\"payload\":\"e30=\",\"sig\":\"c2ln\"}",
        "binding" =>
            "{\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"accountId\":\"acct-1\",\"relayUrl\":\"wss://relay.example.com\",\"issuedAt\":1700000000000,\"teamId\":\"team-1\"}",
        "hello" =>
            "{\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"publicKey\":\"cHVibGlj\",\"signature\":\"c2ln\",\"issuedAt\":1700000000000,\"agentVersion\":\"3.0.0\",\"bootId\":\"0123456789abcdef\",\"resumed\":true,\"lastSeq\":7}",
        "hello.response" =>
            "{\"binding\":{\"v\":1,\"kid\":\"orch-1\",\"payload\":\"e30=\",\"sig\":\"c2ln\"},\"commands\":[{\"v\":1,\"kid\":\"orch-1\",\"payload\":\"e30=\",\"sig\":\"c2ln\"}],\"serverTime\":1700000000500}",
        "ack" =>
            "{\"seq\":7,\"status\":\"rejected\",\"code\":\"bad_signature\"}",
        "ack.applied" =>
            "{\"seq\":8,\"status\":\"applied\"}",
        "usage" =>
            "{\"v\":1,\"batchId\":\"dev_abcdefghjkmnpqrstvwxyz0123:3\",\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"seq\":3,\"from\":1700000000000,\"to\":1700000060000,\"runs\":2,\"inputTokens\":1000,\"outputTokens\":500,\"cacheTokens\":250,\"computeSeconds\":60,\"storageBytes\":123456789}",
        "lifecycle.sleeping" =>
            "{\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"state\":\"sleeping\",\"reason\":\"idle\",\"at\":1700000000000,\"wakeAt\":1700003600000,\"summary\":{\"interruptedRuns\":1,\"killedShells\":2,\"queuedMessages\":3}}",
        "lifecycle.awake" =>
            "{\"v\":1,\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\",\"state\":\"awake\",\"reason\":\"wake\",\"at\":1700000000000}",
        "ack.response" =>
            "{\"ok\":true,\"duplicate\":true}",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
