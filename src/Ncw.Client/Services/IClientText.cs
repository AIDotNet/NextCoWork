namespace Ncw.Client.Services;

/// <summary>
/// Client 层需要给用户看的少数文字(默认组名、弹窗标题)。Client 与语言无关,由 UI 层按当前语言提供实现。
/// </summary>
public interface IClientText
{
    string NewGroupDefaultName { get; }

    string RenameGroupTitle { get; }

    string RenameGroupDescription { get; }

    string NameLabel { get; }

    string LocalDeviceName { get; }

    string AddProjectPickTitle { get; }

    string AddProjectFailedTitle { get; }

    string DeviceNotConnected { get; }

    string RenameSessionTitle { get; }

    string RenameSessionDescription { get; }

    string DeleteSessionTitle { get; }

    string DeleteSessionMessage(string sessionTitle);

    string DeleteLabel { get; }

    string DeleteProviderTitle { get; }

    string DeleteProviderMessage(string providerName);

    string RemoveProjectTitle { get; }

    string RemoveProjectMessage(string projectName);

    string RemoveProjectLabel { get; }

    string RemoveProjectFailedTitle { get; }

    string ProjectRemoveOffline { get; }

    string ProjectRemoveFailed { get; }

    /// <summary>模版端点标签(<c>intl</c> / <c>anthropic</c>)的文字,「添加供应商」的搜索也按它匹配。默认原样返回。</summary>
    string TemplateVariant(string variant) => variant;
}

/// <summary>没有接 UI 时(单测)的默认文字。</summary>
public sealed class DefaultClientText : IClientText
{
    public static readonly DefaultClientText Instance = new();

    public string NewGroupDefaultName => "新项目组";

    public string RenameGroupTitle => "重命名项目组";

    public string RenameGroupDescription => "名字会同步到你的所有设备。";

    public string NameLabel => "名称";

    public string LocalDeviceName => "本机";

    public string AddProjectPickTitle => "选择项目文件夹";

    public string AddProjectFailedTitle => "无法添加项目";

    public string DeviceNotConnected => "本机 Agent 尚未连接,请稍后再试。";

    public string RenameSessionTitle => "重命名会话";

    public string RenameSessionDescription => "新名称会显示在会话列表里。";

    public string DeleteSessionTitle => "删除会话";

    public string DeleteSessionMessage(string sessionTitle) => $"确定删除「{sessionTitle}」?它的全部消息和子代理记录都会被删除,无法恢复。";

    public string DeleteLabel => "删除";

    public string DeleteProviderTitle => "删除供应商";

    public string DeleteProviderMessage(string providerName) => $"确定删除供应商「{providerName}」?它的模型别名和已保存的凭据都会被删除。";

    public string RemoveProjectTitle => "移除项目";

    public string RemoveProjectMessage(string projectName) => $"从侧边栏移除「{projectName}」?文件夹里的文件不会删除,但这个项目的会话以后不会再显示(重新添加同一个文件夹也不会回来)。";

    public string RemoveProjectLabel => "移除";

    public string RemoveProjectFailedTitle => "无法移除项目";

    public string ProjectRemoveOffline => "这个项目所在的设备没有连接,连上之后再移除。";

    public string ProjectRemoveFailed => "设备没能移除这个项目,请稍后再试。";
}
