using Ncw.Desktop;

namespace Ncw.Desktop.Tests;

public class SystemMotionProbeTests
{
    [Fact]
    public void The_probe_never_throws_and_answers_on_macos_and_windows()
    {
        var answer = SystemMotionProbe.ReducesMotion();   // 读不到(Linux、探测失败)是 null,不是异常

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            Assert.NotNull(answer);                        // macOS 上实测:NSWorkspace.accessibilityDisplayShouldReduceMotion;值取决于这台机器的系统设置
        }
        else
        {
            Assert.Null(answer);
        }
    }
}
