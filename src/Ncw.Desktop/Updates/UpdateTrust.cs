using System.Reflection;
using Ncw.Client.Updates;

namespace Ncw.Desktop.Updates;

/// <summary>
/// 内置的发布公钥(docs/design/04 §6)。来源只有一个:<b>构建时</b>写进程序集元数据的 <c>NcwReleasePublicKeys</c>
/// (<c>kid:base64,kid:base64</c>;<c>tools/Ncw.Build publish-desktop</c> 从 <c>NCW_RELEASE_PUBLIC_KEYS</c> 传入)。
/// 运行时<b>没有任何环境变量或配置文件能改它</b>:信任根是构建,不是运行环境。没有内置公钥的构建(开发)得到空列表,自更新会拒绝一切清单。
/// 轮换:发布新版本时把新公钥加进去,旧版本继续信旧的;确认用户都升过之后,再从后续构建里删掉旧的(删除即吊销)。
/// </summary>
public static class UpdateTrust
{
    public const string MetadataKey = "NcwReleasePublicKeys";

    public static ReleaseKeyRing BuiltIn(Assembly? assembly = null) =>
        FromMetadata((assembly ?? typeof(UpdateTrust).Assembly).GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == MetadataKey)?.Value);

    /// <summary>元数据文本 → 信任列表。没有、或任何一项不合法 → 空列表(整份作废,不部分采用)。</summary>
    public static ReleaseKeyRing FromMetadata(string? text) => ReleaseKeyRing.TryParse(text, out var ring) ? ring : ReleaseKeyRing.Empty;
}

/// <summary>
/// 桌面端自更新的配置。feed 位置可配置:默认指向设计文档约定的后端(<see cref="FeedLocation.DefaultTemplate"/>),
/// 开发 / 测试用 <c>NCW_UPDATE_FEED</c> 指向本地目录或本地 HTTP 服务。
/// </summary>
public sealed record UpdateConfiguration(string FeedTemplate, ReleaseKeyRing Keys, string Rid)
{
    public const string FeedEnv = "NCW_UPDATE_FEED";

    public static UpdateConfiguration FromEnvironment(Func<string, string?> env, ReleaseKeyRing? keys = null) =>
        new(env(FeedEnv) is { Length: > 0 } custom ? custom : FeedLocation.DefaultTemplate, keys ?? UpdateTrust.BuiltIn(), PlatformId.Current);
}

/// <summary>平台标识(和 <c>tools/Ncw.Build</c> 的 RID 同一套):<c>osx-arm64</c> / <c>win-x64</c> / <c>linux-x64</c>…</summary>
public static class PlatformId
{
    public static string Current
    {
        get
        {
            var os = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            return $"{os}-{arch}";
        }
    }
}
