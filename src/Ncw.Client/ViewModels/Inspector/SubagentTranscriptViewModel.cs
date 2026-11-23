using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 点开一个子代理看到的只读转录(右侧面板)。内容是子会话在 Store 里的镜像(<c>SubagentActions.OpenAsync</c> 订阅了子会话主题;子 Run 还在跑时再订阅它自己的
/// <c>run/&lt;childRunId&gt;</c> 主题,逐字事件和主智能体走同一个归约函数),行由 <see cref="TranscriptBuilder"/> 从 Store 派生——和主对话同一条路径,不另起一套。
/// 转录里的审批 / 目标 / 压缩这类行不在只读面板里显示;嵌套的子代理卡片显示为一行(没有操作)。
/// </summary>
public sealed partial class SubagentTranscriptViewModel(SessionRef child, string title, Func<string, string> deviceName, IImageSource? images = null,
    AttachmentActions? mediaAttachments = null) : ObservableObject
{
    private static readonly RelayCommand<SubagentRef> NoStop = new(static _ => { }, static _ => false);

    public SessionRef Child { get; } = child;

    public string Title { get; } = title;

    public ObservableCollection<RowViewModel> Rows { get; } = [];

    /// <summary>子会话的快照还没到。</summary>
    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;

    /// <summary>设备断开:运行中的状态未知(规则 7),界面显示「连接中…」,不继续计时。</summary>
    [ObservableProperty] public partial bool IsUnknown { get; private set; }

    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    /// <summary>Store 变了之后重新派生。</summary>
    public void Refresh(ConversationState state, DateTimeOffset now, bool deviceUnknown)
    {
        IsUnknown = deviceUnknown;
        IsLoading = !state.Sessions.ContainsKey(Child) && !deviceUnknown;
        var rows = TranscriptBuilder.Build(state, Child, null, now, deviceUnknown)
            .Where(static r => r is UserRow or AssistantRow or ThinkingRow or ToolRow or SubagentRow or ErrorRow)
            .ToImmutableArray();
        Reconcile(rows);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Reconcile(ImmutableArray<TranscriptRow> rows)
    {
        var existing = Rows.ToDictionary(static r => r.Key, StringComparer.Ordinal);
        var keys = rows.Select(static r => r.Key).ToList();
        if (Rows.Select(static r => r.Key).SequenceEqual(keys))
        {
            for (var i = 0; i < rows.Length; i++)
            {
                Rows[i].Update(rows[i]);
            }

            return;
        }

        Rows.Clear();
        foreach (var row in rows)
        {
            if (existing.TryGetValue(row.Key, out var vm))
            {
                vm.Update(row);
                Rows.Add(vm);
            }
            else
            {
                Rows.Add(Create(row));
            }
        }
    }

    private RowViewModel Create(TranscriptRow row) => row switch
    {
        UserRow r => new UserRowViewModel(r),
        AssistantRow r => new AssistantRowViewModel(r),
        ThinkingRow r => new ThinkingRowViewModel(r),
        ToolRow r => new ToolRowViewModel(r, deviceName, deviceId: Child.DeviceId, images: images, attachments: mediaAttachments),
        SubagentRow r => new SubagentRowViewModel(r, NoStop, new RelayCommand<SubagentRef>(static _ => { })),
        ErrorRow r => new ErrorRowViewModel(r, new AsyncRelayCommand(static () => Task.CompletedTask, static () => false)),
        _ => throw new InvalidOperationException($"Unexpected row {row.GetType().Name}"),
    };
}
