using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>供应商自动发现要用到的设备意图(<see cref="ProvidersActions"/> 实现;测试换成假的)。</summary>
public interface IProviderDiscoveryCommands
{
    bool SupportsDiscovery(string deviceId);

    Task<ActionResult<DiscoveryScanResult>> ScanDiscoveryAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<DiscoveryImportResult>> ImportDiscoveredAsync(string deviceId, IReadOnlyList<string> candidateIds, CancellationToken ct = default);
}

/// <summary>发现页的状态。</summary>
public enum DiscoveryState
{
    /// <summary>还没扫(或换了设备)。</summary>
    Idle,

    /// <summary>设备不支持自动发现(老设备),或这个角色不能用(owner 才行)。</summary>
    Unsupported,

    Scanning,

    Ready,

    Failed,
}

/// <summary>扫到的一条候选(一次性的查询结果,不进 Store;勾选、导入结局是纯 UI 状态)。</summary>
public sealed partial class DiscoveredItemViewModel(DiscoveredProviderDto dto) : ObservableObject
{
    public DiscoveredProviderDto Dto { get; } = dto;

    public string Id => Dto.Id;

    public string Name => Dto.Name;

    public string Source => Dto.Source;

    public string Origin => Dto.Origin;

    public string BaseUrl => Dto.BaseUrl;

    public string Protocol => Dto.Protocol;

    public string? KeyLast4 => Dto.KeyLast4;

    public bool HasKey => Dto.KeyLast4 is not null;

    public string Status => Dto.Status;

    public bool IsImported => Dto.Status == DiscoveryStatus.Imported;

    public bool IsConflict => Dto.Status == DiscoveryStatus.Conflict;

    /// <summary>已经导入过的不能再选。</summary>
    public bool CanSelect => !IsImported;

    public string ModelsText => string.Join(", ", Dto.Models);

    public bool HasModels => Dto.Models.Length > 0;

    /// <summary>也见于(其他来源)。</summary>
    public IReadOnlyList<string> AlsoSeenIn => Dto.AlsoSeenIn;

    public bool HasAlsoSeenIn => Dto.AlsoSeenIn.Length > 0;

    /// <summary>logo 的键(按地址认出的模版所属服务;模型页填);认不出为 null。</summary>
    [ObservableProperty] public partial string? Logo { get; set; }

    /// <summary>新的默认勾上;冲突的默认不勾(导入会多出一个带后缀的供应商,让用户自己决定)。</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; } = dto.Status == DiscoveryStatus.New;

    /// <summary>上一次导入这条失败的原因(稳定 key,界面翻译)。</summary>
    [ObservableProperty] public partial string? ErrorKey { get; set; }
}

/// <summary>
/// 模型页的「从其他应用导入」(docs/design/07 §3「供应商自动发现与导入」):打开模型页时自动扫一次,列出候选,勾选后导入。
/// 候选是设备上的一次性查询结果(不是设备的持续事实,所以不进 Store);导入后供应商经 <c>providers</c> 主题回到 Store。密钥明文从不经过这里。
/// </summary>
public sealed partial class ProviderDiscoveryViewModel : ObservableObject
{
    private readonly IProviderDiscoveryCommands _commands;
    private readonly Action<string> _imported;
    private int _generation;

    /// <param name="imported">导入成功后调用(第一个新建的供应商 id),模型页据此选中它。</param>
    public ProviderDiscoveryViewModel(IProviderDiscoveryCommands commands, Action<string> imported)
    {
        _commands = commands;
        _imported = imported;
    }

    public ObservableCollection<DiscoveredItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial DiscoveryState State { get; private set; }

    [ObservableProperty] public partial bool IsImporting { get; private set; }

    /// <summary>上一次导入成功的条数(显示一句确认;重新扫描后清掉)。</summary>
    [ObservableProperty] public partial int LastImportedCount { get; private set; }

    /// <summary>扫描失败的原因(稳定 key)。</summary>
    [ObservableProperty] public partial string? ErrorKey { get; private set; }

    public string? DeviceId { get; private set; }

    public bool IsScanning => State == DiscoveryState.Scanning;

    public bool IsReady => State == DiscoveryState.Ready;

    public bool IsUnsupported => State == DiscoveryState.Unsupported;

    public bool IsFailed => State == DiscoveryState.Failed;

    public bool HasItems => Items.Count > 0;

    /// <summary>扫完了,一条都没有。</summary>
    public bool IsEmpty => State == DiscoveryState.Ready && Items.Count == 0;

    /// <summary>还能导入的条数(列表栏上的数字):新的 + 冲突的。</summary>
    public int ImportableCount => Items.Count(i => i.CanSelect);

    public bool HasImportable => ImportableCount > 0;

    public bool HasImported => LastImportedCount > 0;

    public bool CanImport => !IsImporting && State == DiscoveryState.Ready && Items.Any(i => i.IsChecked && i.CanSelect);

    partial void OnStateChanged(DiscoveryState value)
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsUnsupported));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanImport));
        ImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsImportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImport));
        ImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastImportedCountChanged(int value) => OnPropertyChanged(nameof(HasImported));

    /// <summary>
    /// 模型页对着某台设备、而且能写(owner)时调:第一次(或换了设备)自动扫一次。设备不支持这个能力 / 不能写就标成不支持,不发请求。
    /// </summary>
    public void Activate(string deviceId, bool canEdit)
    {
        if (DeviceId == deviceId && State is not DiscoveryState.Idle and not DiscoveryState.Unsupported)
        {
            return;
        }

        if (DeviceId != deviceId)
        {
            DeviceId = deviceId;
            Clear();
        }

        if (!canEdit || !_commands.SupportsDiscovery(deviceId))
        {
            State = DiscoveryState.Unsupported;
            return;
        }

        _ = ScanAsync();
    }

    /// <summary>设备换了 / 不可用了:清掉上一台设备的结果。</summary>
    public void Reset()
    {
        DeviceId = null;
        Clear();
        State = DiscoveryState.Idle;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (DeviceId is not { } device)
        {
            return;
        }

        var generation = ++_generation;
        State = DiscoveryState.Scanning;
        ErrorKey = null;
        LastImportedCount = 0;
        var result = await _commands.ScanDiscoveryAsync(device).ConfigureAwait(true);
        if (generation != _generation || DeviceId != device)
        {
            return; // 期间换了设备或又扫了一次
        }

        if (result is { IsOk: true, Value: { } scan })
        {
            Fill(scan.Candidates);
            State = DiscoveryState.Ready;
        }
        else if (result.Outcome == ActionOutcome.Unsupported)
        {
            State = DiscoveryState.Unsupported;
        }
        else
        {
            ErrorKey = result.ErrorKey ?? result.Outcome.ToString();
            State = DiscoveryState.Failed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (DeviceId is not { } device)
        {
            return;
        }

        var chosen = Items.Where(i => i.IsChecked && i.CanSelect).Select(i => i.Id).ToList();
        IsImporting = true;
        try
        {
            var result = await _commands.ImportDiscoveredAsync(device, chosen).ConfigureAwait(true);
            if (result is not { IsOk: true, Value: { } import })
            {
                ErrorKey = result.ErrorKey ?? result.Outcome.ToString();
                return;
            }

            var failed = import.Items.Where(i => i.ErrorKey is not null).ToDictionary(i => i.CandidateId, i => i.ErrorKey, StringComparer.Ordinal);
            var created = import.Items.Where(i => i.ProviderId is not null).Select(i => i.ProviderId!).ToList();
            await ScanAsync().ConfigureAwait(true); // 状态以设备为准:导入过的变成「已导入」
            foreach (var item in Items)
            {
                item.ErrorKey = failed.GetValueOrDefault(item.Id);
            }

            LastImportedCount = created.Count;
            if (created.Count > 0)
            {
                _imported(created[0]);
            }
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void Fill(IEnumerable<DiscoveredProviderDto> candidates)
    {
        Clear();
        foreach (var dto in candidates)
        {
            var item = new DiscoveredItemViewModel(dto);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DiscoveredItemViewModel.IsChecked))
                {
                    OnPropertyChanged(nameof(CanImport));
                    ImportCommand.NotifyCanExecuteChanged();
                }
            };
            Items.Add(item);
        }

        RaiseItems();
    }

    private void Clear()
    {
        Items.Clear();
        RaiseItems();
    }

    private void RaiseItems()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ImportableCount));
        OnPropertyChanged(nameof(HasImportable));
        OnPropertyChanged(nameof(CanImport));
        ImportCommand.NotifyCanExecuteChanged();
    }
}
