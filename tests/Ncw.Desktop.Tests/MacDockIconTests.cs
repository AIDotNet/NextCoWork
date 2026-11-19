using Xunit;

namespace Ncw.Desktop.Tests;

public class MacDockIconTests
{
    [Theory]
    [InlineData("/Applications/NextCoWork.app/Contents/MacOS/", true)]
    [InlineData("/Applications/NextCoWork.app/Contents/MacOS", true)]
    [InlineData("/Users/me/code/NextCoWork/src/Ncw.Desktop/bin/Debug/net11.0/", false)] // dotnet run:没有包,要自己设 Dock 图标
    [InlineData("/Users/me/NextCoWork.app/Contents/MacOS/agent/", false)]              // sidecar 目录不是桌面可执行文件所在目录
    public void Detects_whether_the_process_runs_from_inside_an_app_bundle(string baseDirectory, bool expected) =>
        Assert.Equal(expected, MacDockIcon.IsInsideAppBundle(baseDirectory));

    [Fact]
    public void The_dock_icon_png_is_embedded_in_the_desktop_assembly()
    {
        using var stream = typeof(MacDockIcon).Assembly.GetManifestResourceStream("Ncw.Desktop.AppIcon.png");

        Assert.NotNull(stream);
        var header = new byte[8];
        stream.ReadExactly(header);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header); // PNG 签名
    }
}
