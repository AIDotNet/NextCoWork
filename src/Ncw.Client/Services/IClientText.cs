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
}

/// <summary>没有接 UI 时(单测)的默认文字。</summary>
public sealed class DefaultClientText : IClientText
{
    public static readonly DefaultClientText Instance = new();

    public string NewGroupDefaultName => "新项目组";

    public string RenameGroupTitle => "重命名项目组";

    public string RenameGroupDescription => "名字会同步到你的所有设备。";

    public string NameLabel => "名称";
}
