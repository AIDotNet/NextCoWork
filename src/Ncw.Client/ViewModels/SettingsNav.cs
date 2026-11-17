namespace Ncw.Client.ViewModels;

/// <summary>
/// 设置页导航里的一项(docs/design/07 §3「信息架构」,方案 D)。17 个分区(<see cref="SettingsSection"/>)合成 11 个导航项;
/// 有多个分区的导航项,分区在列表栏里列出来(子分区)。
/// </summary>
public enum SettingsPage
{
    Account,
    Appearance,
    Shortcuts,
    Notifications,
    About,
    General,
    Models,
    Extensions,
    Connection,
    Data,
    Project,
}

/// <summary>列表栏里的一个子分区(<paramref name="IsCurrent"/> 从 <see cref="SettingsViewModel.Section"/> 派生)。</summary>
public sealed record SettingsSubItem(SettingsSection Section, bool IsCurrent);

/// <summary>分区与导航项的对应:只有这一处(纯 UI 映射,不是设备上的事实,所以在 Client 而不在 Core/Domain)。</summary>
public static class SettingsNav
{
    private static readonly SettingsSection[] ExtensionSections =
        [SettingsSection.Skills, SettingsSection.Commands, SettingsSection.Hooks, SettingsSection.Mcp, SettingsSection.Plugins];

    private static readonly SettingsSection[] ConnectionSections = [SettingsSection.Remote, SettingsSection.Gateway, SettingsSection.Devices];

    private static readonly SettingsSection[] DataSections = [SettingsSection.Usage, SettingsSection.Storage];

    /// <summary>导航项按显示顺序。</summary>
    public static IReadOnlyList<SettingsPage> Pages { get; } = Enum.GetValues<SettingsPage>();

    /// <summary>这个导航项下的分区(按列表栏顺序;只有一个分区的导航项没有列表栏)。</summary>
    public static IReadOnlyList<SettingsSection> SectionsOf(SettingsPage page) => page switch
    {
        SettingsPage.Account => [SettingsSection.Account],
        SettingsPage.Appearance => [SettingsSection.Appearance],
        SettingsPage.Shortcuts => [SettingsSection.Shortcuts],
        SettingsPage.Notifications => [SettingsSection.Notifications],
        SettingsPage.About => [SettingsSection.About],
        SettingsPage.General => [SettingsSection.General],
        SettingsPage.Models => [SettingsSection.Models],
        SettingsPage.Extensions => ExtensionSections,
        SettingsPage.Connection => ConnectionSections,
        SettingsPage.Data => DataSections,
        SettingsPage.Project => [SettingsSection.Project],
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    /// <summary>分区所在的导航项。</summary>
    public static SettingsPage PageOf(SettingsSection section) => section switch
    {
        SettingsSection.Account => SettingsPage.Account,
        SettingsSection.Appearance => SettingsPage.Appearance,
        SettingsSection.Shortcuts => SettingsPage.Shortcuts,
        SettingsSection.Notifications => SettingsPage.Notifications,
        SettingsSection.About => SettingsPage.About,
        SettingsSection.General => SettingsPage.General,
        SettingsSection.Models => SettingsPage.Models,
        SettingsSection.Skills or SettingsSection.Commands or SettingsSection.Hooks or SettingsSection.Mcp or SettingsSection.Plugins => SettingsPage.Extensions,
        SettingsSection.Remote or SettingsSection.Gateway or SettingsSection.Devices => SettingsPage.Connection,
        SettingsSection.Usage or SettingsSection.Storage => SettingsPage.Data,
        SettingsSection.Project => SettingsPage.Project,
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    /// <summary>导航项有列表栏:有子分区,或者本身是「一组东西」(模型与供应商:供应商列表 → 详情)。</summary>
    public static bool HasList(SettingsPage page) => page == SettingsPage.Models || SectionsOf(page).Count > 1;
}
