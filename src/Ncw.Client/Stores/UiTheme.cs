namespace Ncw.Client.Stores;

/// <summary>
/// 用户的外观主题偏好(docs/design/03 §4、07 §3)。<see cref="Dark"/> 是默认;
/// <see cref="System"/> 跟随操作系统的明暗模式。存在 <see cref="UiPrefs.Theme"/>,下次启动恢复。
/// </summary>
public enum UiThemePreference
{
    Dark,
    Light,
    System,
}
