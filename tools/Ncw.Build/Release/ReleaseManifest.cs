namespace Ncw.Build.Release;

/// <summary>
/// 发布清单(docs/design/04 §2、§7.4「后端」,05 B2 / B3):和产物一起放在 feed 目录 <c>/api/client/updates/v3/{channel}/{rid}/</c>,
/// 3.x 客户端更新前用内置公钥校验它的 Ed25519 签名,并按它放行包;后端 <c>/v3/latest</c> 从它汇总各平台的版本与安装包。
/// 签名是**分离签名**:对 <c>release-manifest.json</c> 的原始字节签,签名文件是 <c>release-manifest.json.sig</c>(base64)。
/// 这样验证方(2.9 里的 JS 或任何语言)不需要做 JSON 规范化。
/// </summary>
internal sealed record ReleaseManifest(
    int Schema,
    string Product,
    string Version,
    string Channel,
    DateTimeOffset CreatedUtc,
    ReleasePolicy Policy,
    string? Notes,
    ReleaseArtifact[] Artifacts,
    ManifestSigning Signing)
{
    public const int CurrentSchema = 1;
}

/// <summary>强制更新语义沿用 2.x(docs/design/04 §3):mandatory / minimumSupportedVersion / graceUntil。</summary>
internal sealed record ReleasePolicy(bool Mandatory, string? MinimumSupportedVersion, DateTimeOffset? GraceUntil);

/// <param name="Platform">RID,例如 osx-arm64。</param>
/// <param name="Kind">velopack-setup / velopack-full / velopack-delta / portable / bundle-zip / … 由文件名推断或显式指定。</param>
/// <param name="Url">下载地址:上传到后端之后才知道,生成清单时为 null(下载地址 = feed 目录 + 文件名,不需要回填)。</param>
internal sealed record ReleaseArtifact(string Platform, string Kind, string FileName, string Sha256, long Size, string? Url);

/// <summary>
/// 签名信息。<see cref="Status"/>:<c>signed</c> 表示旁边有分离签名;<c>pending</c> 表示没有提供密钥,签名待补(CI 在持有密钥的环节再跑一次)。
/// 字段本身在被签的字节里,所以 keyId 不会被替换。
/// </summary>
internal sealed record ManifestSigning(string Algorithm, string? KeyId, string Status, string? SignatureFile);
