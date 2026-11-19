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

    string RenameAliasTitle { get; }

    string RenameAliasDescription { get; }

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

    public string RenameAliasTitle => "重命名模型别名";

    public string RenameAliasDescription => "别名是会话里选择模型时用的名字。";
}
