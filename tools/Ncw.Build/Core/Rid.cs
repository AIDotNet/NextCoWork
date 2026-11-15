using System.Runtime.InteropServices;

namespace Ncw.Build.Core;

internal enum OsFamily
{
    MacOs,
    Windows,
    Linux,
}

/// <summary>.NET 运行时标识。3.0 桌面三平台(docs/design/05 M1):macOS / Windows / Linux,x64 与 arm64。</summary>
internal readonly record struct Rid(OsFamily Os, string Arch)
{
    private static readonly string[] Architectures = ["x64", "arm64"];

    public string Value => $"{Prefix(Os)}-{Arch}";

    public bool IsUnix => Os != OsFamily.Windows;

    /// <summary>ncw-agent 的文件名(AGENTS.md §1:这是桌面和云端调用的程序,不是用户命令)。</summary>
    public string AgentExecutable => Os == OsFamily.Windows ? "ncw-agent.exe" : "ncw-agent";

    /// <summary>服务器中继的文件名(服务端部署物,不是用户命令)。</summary>
    public string RelayExecutable => Os == OsFamily.Windows ? "ncw-relay.exe" : "ncw-relay";

    public string DesktopExecutable => Os == OsFamily.Windows ? "NextCoWork.exe" : "NextCoWork";

    public override string ToString() => Value;

    public static bool TryParse(string? text, out Rid rid)
    {
        rid = default;
        if (text is null)
        {
            return false;
        }

        var dash = text.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        OsFamily os;
        switch (text[..dash])
        {
            case "osx": os = OsFamily.MacOs; break;
            case "win": os = OsFamily.Windows; break;
            case "linux": os = OsFamily.Linux; break;
            default: return false;
        }

        var arch = text[(dash + 1)..];
        if (Array.IndexOf(Architectures, arch) < 0)
        {
            return false;
        }

        rid = new Rid(os, arch);
        return true;
    }

    public static Rid Parse(string text) =>
        TryParse(text, out var rid)
            ? rid
            : throw new BuildException($"Unsupported runtime identifier '{text}'. Supported: osx-arm64, osx-x64, win-x64, win-arm64, linux-x64, linux-arm64.");

    public static Rid Host()
    {
        var os = OperatingSystem.IsMacOS() ? OsFamily.MacOs : OperatingSystem.IsWindows() ? OsFamily.Windows : OsFamily.Linux;
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return new Rid(os, arch);
    }

    /// <summary>
    /// NativeAOT 需要目标平台的原生链接器和 SDK,不能跨操作系统编译(同一系统不同架构可以,macOS x64 / arm64 互相就行)。
    /// 返回 null 表示可以;否则是给开发者的原因。
    /// </summary>
    public string? CannotNativeAotFrom(Rid host) =>
        Os == host.Os
            ? null
            : $"NativeAOT cannot cross-compile from {host} to {this}: run this step on a {Prefix(Os)} machine (CI matrix).";

    private static string Prefix(OsFamily os) => os switch
    {
        OsFamily.MacOs => "osx",
        OsFamily.Windows => "win",
        _ => "linux",
    };
}
