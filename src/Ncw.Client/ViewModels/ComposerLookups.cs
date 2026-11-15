using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>输入框里 <c>@</c> 弹出的一个文件候选(项目相对路径,会作为文件引用发给设备)。</summary>
public sealed record FileSuggestionItem(string Path, string Name, string Directory);

/// <summary>弹层里的一行候选(<see cref="IsActive"/> = 键盘高亮)。</summary>
public sealed partial class FileSuggestionRow(FileSuggestionItem item) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public FileSuggestionItem Item { get; } = item;

    public string Path => Item.Path;

    public string Name => Item.Name;

    public string Directory => Item.Directory;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] public partial bool IsActive { get; set; }
}

/// <summary>消息里的一个文件引用(输入框上方的 chip)。</summary>
public sealed record FileRefChip(string Path, string Name)
{
    public string Directory => Path.LastIndexOf('/') is > 0 and var i ? Path[..i] : "";
}

/// <summary>
/// 输入框向设备查的两样东西:<c>@</c> 的文件名候选(<c>files.suggest</c>)和 <c>/</c> 的命令清单(<c>commands.list</c>),以及发送前的命令展开
/// (规则只有 Core 的 <c>CommandExpansion</c> 一份,展开在客户端完成:设备收到的永远是普通文本)。失败返回 null,界面把草稿还给用户。
/// </summary>
public interface IComposerLookups
{
    Task<IReadOnlyList<FileSuggestionItem>?> SuggestFilesAsync(SessionRef session, string workspaceId, string query, CancellationToken ct);

    Task<IReadOnlyList<CommandDto>?> ListCommandsAsync(SessionRef session, string workspaceId, CancellationToken ct);

    Task<string?> ExpandAsync(SessionRef session, string workspaceId, string draft, CancellationToken ct);
}

/// <summary><see cref="IComposerLookups"/> 的真实现:<see cref="FilesActions"/> + <see cref="CommandsActions"/>。</summary>
public sealed class ComposerLookups(IFilesCommands files, CommandsActions commands) : IComposerLookups
{
    public async Task<IReadOnlyList<FileSuggestionItem>?> SuggestFilesAsync(SessionRef session, string workspaceId, string query, CancellationToken ct)
    {
        var result = await files.SuggestAsync(session.DeviceId, workspaceId, query, 12, ct).ConfigureAwait(false);
        return result.IsOk && result.Value is { } value
            ? [.. value.Suggestions.Select(static s => new FileSuggestionItem(s.Path, s.Name, s.Path.LastIndexOf('/') is > 0 and var i ? s.Path[..i] : ""))]
            : null;
    }

    public async Task<IReadOnlyList<CommandDto>?> ListCommandsAsync(SessionRef session, string workspaceId, CancellationToken ct)
    {
        var result = await commands.ListAsync(session.DeviceId, workspaceId, ct).ConfigureAwait(false);
        return result.IsOk && result.Value is { } value ? value.Commands : null;
    }

    public async Task<string?> ExpandAsync(SessionRef session, string workspaceId, string draft, CancellationToken ct)
    {
        var result = await commands.ExpandAsync(session.DeviceId, workspaceId, draft, ct).ConfigureAwait(false);
        return result.IsOk ? result.Value : null;
    }
}
