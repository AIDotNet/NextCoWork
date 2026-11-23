using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Ncw.UI.Controls;
using Ncw.UI.Localization;

namespace Ncw.UI.Tests;

/// <summary>终端右键菜单(issue #194):复制 / 粘贴 / 清屏;清屏后让 shell 重画提示符(cmd 除外)。</summary>
public class TerminalMenuTests
{
    private static void Dispatch()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static NcwTerminal Show()
    {
        var terminal = new NcwTerminal();
        var window = new Window { Width = 640, Height = 360, Content = terminal };
        window.Show();
        Dispatch();
        Assert.False(terminal.IsUnavailable);
        terminal.Write(Encoding.UTF8.GetBytes("$ dotnet build\r\nBuild succeeded.\r\n$ "));
        Dispatch();
        return terminal;
    }

    [AvaloniaFact]
    public void Right_click_menu_offers_copy_paste_and_clear_and_copy_needs_a_selection()
    {
        var terminal = Show();

        var menu = Assert.IsType<TKCode.Arc.MenuSurface>(TKCode.Arc.ContextMenu.GetMenu(terminal));
        var entries = menu.Items.OfType<TKCode.Arc.MenuEntry>().ToList();
        Assert.Equal(
            [NcwTexts.Get(NcwTextKind.TerminalCopy), NcwTexts.Get(NcwTextKind.TerminalPaste), NcwTexts.Get(NcwTextKind.TerminalClear)],
            entries.Select(e => e.Header as string));
        Assert.False(entries[0].IsEnabled); // 没有选中:复制灰显
        Assert.False(terminal.CopySelection());
    }

    [AvaloniaFact]
    public void Clear_asks_the_shell_to_redraw_its_prompt_except_for_cmd()
    {
        var terminal = Show();
        var sent = new List<byte[]>();
        terminal.Input += sent.Add;

        terminal.Shell = "/bin/zsh";
        terminal.Clear();
        terminal.Shell = @"C:\Windows\System32\cmd.exe";
        terminal.Clear();
        Dispatch();

        var only = Assert.Single(sent);
        Assert.Equal([0x0c], only);
    }
}
