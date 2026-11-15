using System.Windows.Input;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Mobile;

/// <summary>手机页面的小工具。</summary>
public static class MobilePageExtensions
{
    /// <summary>
    /// 给页面的导航栏加上返回按钮(<paramref name="back"/> 接 NavigationStack 的 BackCommand)。
    /// 每个推入的页面都要有可见的返回按钮:右滑手势不能是唯一的返回方式(Arc 文档的约定)。
    /// </summary>
    public static void ShowBack(this NcwPage page, ICommand back)
    {
        if (page.Header is NcwNavbar bar)
        {
            bar.ShowBack = true;
            bar.BackCommand = back;
        }
    }
}
