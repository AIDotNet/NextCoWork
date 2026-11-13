namespace Ncw.Client.Services;

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
