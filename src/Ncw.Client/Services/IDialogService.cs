namespace Ncw.Client.Services;

/// <summary>文件选择框要选什么(界面层按它给标题、类型过滤和默认文件名)。</summary>
public enum FilePickKind
{
    /// <summary>Skill 的 zip 包(上限 3 MB)。</summary>
    SkillZip,

    /// <summary>插件的 zip 包(上限 3 MB)。</summary>
    PluginZip,

    /// <summary>数据导入文件(<c>.json</c>)。</summary>
    ImportData,

    /// <summary>备份文件(设备本机路径;仅本机设备可用)。</summary>
    BackupFile,

    /// <summary>数据导出的保存位置。</summary>
    ExportData,
}

/// <summary>用户选中的一个文件。<see cref="Path"/> 是本机路径(沙盒平台可能为 null);<see cref="OpenRead"/> 按需打开,大文件不会被一次读进内存。</summary>
public sealed record PickedFile(string Name, string? Path, long Size, Func<Task<Stream>> OpenRead);

/// <summary>
/// ViewModel 需要用户输入时通过它弹窗,不直接依赖任何 UI 类型。UI 层用 Arc Dialog 实现;测试里换成脚本化的假实现。
/// </summary>
public interface IDialogService
{
    /// <summary>让用户输入一段文字;取消返回 null。</summary>
    Task<string?> PromptTextAsync(string title, string description, string label, string? initial);

    /// <summary>让用户选一个本机文件夹;取消返回 null。</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>告诉用户一个操作失败了。</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>二次确认(删除会话等危险操作)。取消返回 false;没有界面的实现默认当作取消。</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(false);

    /// <summary>让用户选一个本机文件;取消返回 null。没有界面的实现默认当作取消。</summary>
    Task<PickedFile?> PickFileAsync(FilePickKind kind) => Task.FromResult<PickedFile?>(null);

    /// <summary>让用户选一个保存位置并返回可写的流;取消返回 null。调用方负责关闭流。</summary>
    Task<Stream?> PickSaveStreamAsync(FilePickKind kind, string suggestedName) => Task.FromResult<Stream?>(null);
}

/// <summary>没有界面时(未接入 UI 的测试)的默认实现:一律当作取消。</summary>
public sealed class NoDialogs : IDialogService
{
    public static readonly NoDialogs Instance = new();

    public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) =>
        Task.FromResult<string?>(null);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
}
