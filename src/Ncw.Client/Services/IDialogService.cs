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

    /// <summary>
    /// 弹出「新建项目」窗口(选设备、填路径);添加成功后由 <paramref name="form"/> 的 <see cref="ViewModels.NewProjectViewModel.Completed"/> 关闭,返回是否添加了项目。
    /// 没有界面的实现(测试替身)默认走「直接选本机文件夹并提交」:设备选不了本机文件夹时当作取消。
    /// </summary>
    async Task<bool> ShowNewProjectAsync(ViewModels.NewProjectViewModel form)
    {
        if (!form.CanBrowse)
        {
            return false;
        }

        await form.BrowseCommand.ExecuteAsync(null);
        if (string.IsNullOrWhiteSpace(form.Path))
        {
            return false;
        }

        if (await form.SubmitAsync())
        {
            return true;
        }

        await ShowErrorAsync(form.FailureTitle, form.ErrorDetail ?? form.Error?.ToString() ?? "");
        return false;
    }

    /// <summary>弹出「项目图标」窗口;保存 / 恢复默认后由 <paramref name="form"/> 的 <see cref="ViewModels.ProjectIconViewModel.Completed"/> 关闭。没有界面的实现当作取消。</summary>
    Task<bool> ShowProjectIconAsync(ViewModels.ProjectIconViewModel form) => Task.FromResult(false);

    /// <summary>
    /// 弹出「添加 / 编辑供应商」窗口(docs/design/07 §3「添加供应商」);保存成功或「保留并关闭」后由 <paramref name="form"/> 的
    /// <see cref="ViewModels.ProviderEditorViewModel.Completed"/> 关闭,点「去导入」由 <see cref="ViewModels.ProviderEditorViewModel.DiscoveryRequested"/> 关闭。
    /// 结果看 <paramref name="form"/> 的 <see cref="ViewModels.ProviderEditorViewModel.SavedProviderId"/>。没有界面的实现当作取消。
    /// </summary>
    Task ShowProviderEditorAsync(ViewModels.ProviderEditorViewModel form) => Task.CompletedTask;

    /// <summary>
    /// 弹出「编辑模型」窗口(docs/design/07「编辑模型」);保存成功后由 <paramref name="form"/> 的 <see cref="ViewModels.ModelEditorViewModel.Completed"/> 关闭。
    /// 没有界面的实现当作取消。
    /// </summary>
    Task ShowModelEditorAsync(ViewModels.ModelEditorViewModel form) => Task.CompletedTask;

    /// <summary>
    /// 让用户选一张图片做项目图标,并压缩成 <see cref="ProjectIconSpec.PixelSize"/> 见方的小图(界面层解码,按缩小后的尺寸解码以省内存)。
    /// 取消返回 null;没有界面的实现当作取消。
    /// </summary>
    Task<PickedIcon?> PickIconImageAsync() => Task.FromResult<PickedIcon?>(null);
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
