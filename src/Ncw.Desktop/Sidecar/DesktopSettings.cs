using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ncw.Desktop.Sidecar;

/// <summary>
/// 桌面宿主自己的设置(目前只有「关窗后本机 Agent 继续运行」),存在控制端目录的 <c>desktop.json</c>。
/// 设置页的开关接上之后,会通过 <see cref="ISidecarPolicy"/> 换成读 UiPrefs(「本应用」作用域,07 §3);这里先提供值的来源和默认值(关)。
/// <c>NCW_KEEP_AGENT=1</c> 可以在不碰文件的情况下强制打开(开发、测试)。
/// </summary>
public sealed class DesktopSettings(string path, Func<string, string?> env) : ISidecarPolicy
{
    public bool KeepAgentRunningAfterExit
    {
        get
        {
            if (env("NCW_KEEP_AGENT") is { Length: > 0 } forced)
            {
                return forced == "1";
            }

            try
            {
                return File.Exists(path)
                    && JsonSerializer.Deserialize(File.ReadAllText(path), DesktopSettingsJson.Default.DesktopSettingsDto)?.KeepAgentRunningAfterExit == true;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void SetKeepAgentRunningAfterExit(bool value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new DesktopSettingsDto(value), DesktopSettingsJson.Default.DesktopSettingsDto));
    }
}

/// <summary>文件里的形状。字段名固定,和宿主解耦。</summary>
public sealed record DesktopSettingsDto(bool KeepAgentRunningAfterExit);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DesktopSettingsDto))]
internal sealed partial class DesktopSettingsJson : JsonSerializerContext;
