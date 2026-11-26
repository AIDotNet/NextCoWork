using Xunit;

namespace Ncw.Desktop.Tests;

/// <summary>Windows 系统托盘里能在任何平台上测的部分;Shell_NotifyIcon / TrackPopupMenuEx 本身没有真机自动化(issue #200)。</summary>
public class WindowsTrayTests
{
    [Theory]
    [InlineData("Quit NextCoWork", "Quit NextCoWork")]
    [InlineData("R&D", "R&&D")]          // 系统菜单里 & 是助记符前缀,原样显示要写两个
    [InlineData("&&", "&&&&")]
    public void Menu_text_escapes_ampersands(string text, string expected) =>
        Assert.Equal(expected, WindowsTray.MenuText(text));

    [Fact]
    public void Off_windows_there_is_no_native_tray()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(WindowsTray.Create("NextCoWork", () => [], () => { }));
    }
}
