namespace Ncw.Client.Services;

/// <summary>
/// 右侧检查器需要给用户看的少数文字(弹窗标题 / 确认文案)。Client 与语言无关:UI 层按当前语言提供实现。
/// 失败原因不在这里:ViewModel 只暴露结构化的 <c>Problem</c>(稳定的 <c>files.*</c> / <c>git.*</c> key),由视图翻译。
/// </summary>
public interface IInspectorText
{
    string NameLabel { get; }

    string NewFileTitle { get; }

    string NewFolderTitle { get; }

    string NewItemDescription { get; }

    string RenameTitle { get; }

    string RenameDescription { get; }

    string DeleteTitle { get; }

    string DeleteMessage(string name);

    string DeleteLabel { get; }

    string DiscardTitle { get; }

    string DiscardMessage { get; }

    string DiscardLabel { get; }

    string NewBranchTitle { get; }

    string NewBranchDescription { get; }

    string BranchNameLabel { get; }

    string ErrorTitle { get; }
}

/// <summary>没有接 UI 时(单测)的默认文字。</summary>
public sealed class DefaultInspectorText : IInspectorText
{
    public static readonly DefaultInspectorText Instance = new();

    public string NameLabel => "Name";

    public string NewFileTitle => "New file";

    public string NewFolderTitle => "New folder";

    public string NewItemDescription => "Created inside the selected folder.";

    public string RenameTitle => "Rename";

    public string RenameDescription => "Enter a new name.";

    public string DeleteTitle => "Delete";

    public string DeleteMessage(string name) => $"Move '{name}' to the project trash?";

    public string DeleteLabel => "Delete";

    public string DiscardTitle => "Discard changes";

    public string DiscardMessage => "Your unsaved edits will be lost.";

    public string DiscardLabel => "Discard";

    public string NewBranchTitle => "New branch";

    public string NewBranchDescription => "Creates the branch from the current commit and switches to it.";

    public string BranchNameLabel => "Branch name";

    public string ErrorTitle => "Operation failed";
}

/// <summary>
/// 客户端<b>本机</b>上和文件相关的动作(显示在访达 / 资源管理器里、用系统默认程序打开、另存为、剪贴板)。
/// 文件在设备上:这些动作只对「本机设备 + 本机环境」的项目有意义,远程设备的项目只能复制路径和另存为(内容从设备读来再存到本机)。
/// UI 层用 Avalonia 的 Launcher / StorageProvider / Clipboard 实现;测试里换成记录调用的假实现。
/// </summary>
public interface ILocalFiles
{
    Task CopyTextAsync(string text);

    /// <summary>在系统文件管理器里显示(<paramref name="absolutePath"/> 是本机路径)。</summary>
    Task<bool> RevealAsync(string absolutePath);

    /// <summary>用系统默认程序打开。</summary>
    Task<bool> OpenAsync(string absolutePath);

    /// <summary>弹出「另存为」选择位置并写入。取消返回 false。</summary>
    Task<bool> SaveAsync(string suggestedName, byte[] data);
}

/// <summary>没有界面时的默认实现:什么也不做。</summary>
public sealed class NoLocalFiles : ILocalFiles
{
    public static readonly NoLocalFiles Instance = new();

    public Task CopyTextAsync(string text) => Task.CompletedTask;

    public Task<bool> RevealAsync(string absolutePath) => Task.FromResult(false);

    public Task<bool> OpenAsync(string absolutePath) => Task.FromResult(false);

    public Task<bool> SaveAsync(string suggestedName, byte[] data) => Task.FromResult(false);
}
