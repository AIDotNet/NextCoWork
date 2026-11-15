using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Core.Domain;

namespace Ncw.Client.Updates;

/// <summary>
/// 发布清单(<c>release-manifest.json</c>),由 <c>tools/Ncw.Build manifest</c> 生成并用 Ed25519 做<b>分离签名</b>(<c>release-manifest.json.sig</c>,
/// base64,对清单的<b>原始字节</b>签)。这里的类型只用来读;字段名和 <c>tools/Ncw.Build/Release/ReleaseManifest.cs</c> 一一对应
/// (<c>Ncw.Build.Tests</c> 里有一条用真实生成的清单走一遍这里的解析与验证的契约测试)。docs/design/04 §7。
/// </summary>
public sealed record ReleaseManifest(
    int Schema,
    string Product,
    string Version,
    string Channel,
    DateTimeOffset CreatedUtc,
    ManifestPolicy Policy,
    string? Notes,
    ManifestArtifact[] Artifacts,
    ManifestSigning Signing)
{
    public const int SupportedSchema = 1;

    public const string ProductName = "NextCoWork";

    public UpdateRelease ToRelease() => new(Version, Policy.Mandatory, Policy.MinimumSupportedVersion, Policy.GraceUntil);
}

/// <summary>强制更新策略:mandatory / minimumSupportedVersion / graceUntil(04 §3)。</summary>
public sealed record ManifestPolicy(bool Mandatory, string? MinimumSupportedVersion, DateTimeOffset? GraceUntil);

/// <param name="Platform">RID,例如 <c>osx-arm64</c>。</param>
/// <param name="Kind"><c>velopack-full</c> / <c>velopack-delta</c> / <c>velopack-setup</c> / <c>portable</c> / <c>feed</c> …</param>
public sealed record ManifestArtifact(string Platform, string Kind, string FileName, string Sha256, long Size, string? Url);

public sealed record ManifestSigning(string Algorithm, string? KeyId, string Status, string? SignatureFile);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(UpdateMeta))]
internal sealed partial class UpdateJson : JsonSerializerContext;

/// <summary>跨会话保留的更新偏好与记录(<c>&lt;控制端目录&gt;/update.json</c>):通道、上次检查时间。不含任何凭据。</summary>
public sealed record UpdateMeta(string? Channel, DateTimeOffset? LastCheckedAt);
