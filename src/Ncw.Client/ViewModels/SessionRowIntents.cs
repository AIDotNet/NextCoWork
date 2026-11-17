using Ncw.Client.Actions;
using Ncw.Client.Services;

namespace Ncw.Client.ViewModels;

/// <summary>会话行的意图从哪里落地(桌面侧边栏、手机项目会话页各有一个宿主,最终都转给 <see cref="SessionActions"/>)。</summary>
public interface ISessionRowHost
{
    void Open(SessionItemViewModel item);

    Task StopAsync(SessionItemViewModel item);

    Task RenameAsync(SessionItemViewModel item);

    Task SetFavoriteAsync(SessionItemViewModel item, bool favorited);

    Task SetArchivedAsync(SessionItemViewModel item, bool archived);

    Task DeleteAsync(SessionItemViewModel item);
}

/// <summary>
/// 会话行意图的唯一实现(AGENTS.md §3 规则 4):打开、停止、重命名、收藏、归档、删除都按行自己的项目(<see cref="SessionItemViewModel.Project"/>)转给 <see cref="SessionActions"/>,
/// 重命名弹输入框、删除先二次确认。侧边栏里的会话来自不同项目,所以不能再按「当前选中的项目」落地。
/// </summary>
public sealed class SessionRowIntents(SessionActions? sessions, Func<IDialogService> dialogs, Func<IClientText> text) : ISessionRowHost
{
    public void Open(SessionItemViewModel item) => sessions?.Open(item.Project, item.Id);

    /// <summary>停止这个会话正在跑的 Run(手机会话列表左滑的「停止」;桌面标签栏的停止在输入框上)。</summary>
    public async Task StopAsync(SessionItemViewModel item)
    {
        if (sessions is not null)
        {
            await sessions.StopAsync(item.Project, item.Id);
        }
    }

    public async Task RenameAsync(SessionItemViewModel item)
    {
        if (sessions is null)
        {
            return;
        }

        var t = text();
        var title = await dialogs().PromptTextAsync(t.RenameSessionTitle, t.RenameSessionDescription, t.NameLabel, item.Title);
        if (!string.IsNullOrWhiteSpace(title) && title != item.Title)
        {
            await sessions.RenameAsync(item.Project, item.Id, title);
        }
    }

    public async Task SetFavoriteAsync(SessionItemViewModel item, bool favorited)
    {
        if (sessions is not null)
        {
            await sessions.FavoriteAsync(item.Project, item.Id, favorited);
        }
    }

    public async Task SetArchivedAsync(SessionItemViewModel item, bool archived)
    {
        if (sessions is not null)
        {
            await sessions.ArchiveAsync(item.Project, item.Id, archived);
        }
    }

    /// <summary>删除会话:先二次确认,确认了才发(设备上有 Run 在跑会先中止)。</summary>
    public async Task DeleteAsync(SessionItemViewModel item)
    {
        if (sessions is null)
        {
            return;
        }

        var t = text();
        if (await dialogs().ConfirmAsync(t.DeleteSessionTitle, t.DeleteSessionMessage(item.Title), t.DeleteLabel))
        {
            await sessions.DeleteAsync(item.Project, item.Id);
        }
    }
}
