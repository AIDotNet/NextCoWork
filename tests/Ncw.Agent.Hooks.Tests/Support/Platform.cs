namespace Ncw.Agent.Hooks.Tests.Support;

/// <summary>POSIX only:钩子脚本是 sh;Windows 没有进程组的等价物,CI 与开发机都是 POSIX(与 2.x 的集成测试同一个取舍)。</summary>
public static class Platform
{
    public static bool IsPosix => !OperatingSystem.IsWindows();
}
