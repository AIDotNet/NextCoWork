namespace Ncw.UI.Chrome;

/// <summary>
/// 窗口外框:没有系统标题栏,内容铺到窗口顶端,顶部 40px 这一行由各栏自己的头部拼成(参考 Octopus 的实现)。
/// 这里描述平台留给应用的空间。
/// </summary>
/// <param name="LeftInset">左上角留给系统窗口按钮的宽度(macOS 的红绿灯)。</param>
/// <param name="ShowsCaptionButtons">是否由应用在右上角画最小化、最大化、关闭(Windows、Linux)。</param>
public sealed record WindowChrome(double LeftInset, bool ShowsCaptionButtons)
{
    /// <summary>顶部这一行的高度。这一行里没有被控件处理的按下用来拖动窗口。</summary>
    public const double TopRowHeight = 40;

    /// <summary>按操作系统选择外框,只有这一处判断平台。</summary>
    public static WindowChrome ForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new WindowChrome(78, false) : new WindowChrome(0, true);
}
