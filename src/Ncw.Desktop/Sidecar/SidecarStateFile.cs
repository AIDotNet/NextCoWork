using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ncw.Desktop.Sidecar;

/// <summary>
/// <c>&lt;数据根&gt;/run/sidecar.json</c>:桌面端拉起 Agent 时写的,让下一次启动能认出并接上还在运行的 Agent(「关窗后继续运行」)。
/// <list type="bullet">
/// <item><see cref="Token"/>:本机令牌。文件权限 0600,和 socket 同一个信任边界(同一个用户);Agent 自己只从环境变量拿令牌。</item>
/// <item><see cref="Detached"/>:拉起时没有 stdin 管道和父进程监视,桌面端退出后 Agent 继续运行。</item>
/// </list>
/// 偏离 docs/design/09 §5 的「一次性令牌」:要接上脱离的 Agent,令牌必须跨桌面端会话保留(已写进 04 与 09)。
/// </summary>
public sealed record SidecarState(int Pid, string Token, bool Detached, string? Version, DateTimeOffset StartedUtc);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SidecarState))]
internal sealed partial class SidecarJson : JsonSerializerContext;

public sealed class SidecarStateFile(string path)
{
    public SidecarState? Read()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), SidecarJson.Default.SidecarState) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // 读不懂就当没有:最坏是多拉起一个 Agent,而 Agent 的单实例锁会拦住它
        }
    }

    public void Write(SidecarState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, SidecarJson.Default.SidecarState));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
