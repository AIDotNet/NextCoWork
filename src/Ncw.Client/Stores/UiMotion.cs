namespace Ncw.Client.Stores;

/// <summary>
/// 「动效」这项本应用设置的取值(docs/design/03 §9、07 §3「外观」)。<see cref="System"/> = 跟随操作系统的「减少动态效果」,
/// 其余三项是用户明确的选择。存在 <see cref="UiPrefs.Motion"/>,下次启动恢复。
/// </summary>
public enum UiMotionPreference
{
    /// <summary>跟随操作系统:系统要求减少动态效果就用 <see cref="UiMotionLevel.Reduced"/>,否则 <see cref="UiMotionLevel.Full"/>;读不到系统设置时当作 Full。</summary>
    System,

    /// <summary>完整:位移、缩放、弹簧、淡入淡出全部播放。</summary>
    Full,

    /// <summary>减弱:只保留短淡入淡出和颜色变化,位置与尺寸直接跳到终态,装饰性循环停止。</summary>
    Reduced,

    /// <summary>关闭:没有任何动画,所有变化立即生效。</summary>
    Off,
}

/// <summary>实际生效的动效级别(与 Arc 的 <c>ArcMotionLevel</c> 一一对应,但 Client 不引用 Arc,由 UI 层映射)。</summary>
public enum UiMotionLevel
{
    Full,
    Reduced,
    Off,
}

/// <summary>动效级别的唯一解析函数:用户偏好 × 操作系统的减少动态效果。界面、测试、两端共用。</summary>
public static class UiMotion
{
    /// <param name="preference">用户在「外观」里的选择。</param>
    /// <param name="systemReducesMotion">操作系统是否要求减少动态效果;<c>null</c> = 读不到(Avalonia 12 没有这个 API,各平台由宿主探测)。</param>
    public static UiMotionLevel Resolve(UiMotionPreference preference, bool? systemReducesMotion) => preference switch
    {
        UiMotionPreference.Full => UiMotionLevel.Full,
        UiMotionPreference.Reduced => UiMotionLevel.Reduced,
        UiMotionPreference.Off => UiMotionLevel.Off,
        _ => systemReducesMotion == true ? UiMotionLevel.Reduced : UiMotionLevel.Full,
    };
}
