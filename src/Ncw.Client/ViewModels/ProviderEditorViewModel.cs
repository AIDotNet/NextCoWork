using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>「添加 / 编辑供应商」弹窗要用到的设备意图(<see cref="ProvidersActions"/> 实现;测试换成假的)。</summary>
public interface IProviderEditorCommands
{
    Task<ActionResult<ProviderDto>> UpsertAsync(string deviceId, ProvidersUpsertParams provider, CancellationToken ct = default);

    Task<ActionResult<CredentialInfoDto>> SetApiKeyAsync(string deviceId, string providerId, string apiKey, CancellationToken ct = default);

    Task<ActionResult<ProviderTestResultDto>> TestAsync(string deviceId, string providerId, CancellationToken ct = default);
}

/// <summary>弹窗的两步。</summary>
public enum ProviderEditorStep
{
    /// <summary>选模版(网格)。</summary>
    Pick,

    /// <summary>填 key / 高级字段。编辑已有的供应商直接从这一步开始。</summary>
    Form,
}

/// <summary>模版网格里的一张卡片(<see cref="Dto"/> 为 null = 「自定义」)。</summary>
public sealed class ProviderTemplateItem(ProviderTemplateDto? dto)
{
    public ProviderTemplateDto? Dto { get; } = dto;

    public bool IsCustom => Dto is null;

    public string Id => Dto?.Id ?? ProviderTemplateItems.CustomId;

    /// <summary>logo 的键(所属服务);自定义为 null。</summary>
    public string? Service => Dto?.Service;

    /// <summary>品牌名;自定义的名字由界面给(本地化)。</summary>
    public string? Name => Dto?.Name;

    /// <summary>端点标签的 key(<see cref="ProviderTemplateVariants"/>),界面翻译。</summary>
    public string? Variant => Dto?.Variant;

    public string? BaseUrl => Dto?.BaseUrl;

    /// <summary>卡片第二行:主机名(自定义为 null,界面给说明文字)。</summary>
    public string? Host => Dto is { } d && Uri.TryCreate(d.BaseUrl, UriKind.Absolute, out var uri) ? (uri.IsLoopback ? $"localhost:{uri.Port}" : uri.Host) : null;

    public bool NeedsKey => Dto?.NeedsKey ?? true;
}

/// <summary>网格里的一组。<see cref="Key"/>:<c>featured</c> / <see cref="ProviderTemplateGroups"/> 的值 / <c>other</c> / <c>results</c>(搜索结果)。</summary>
public sealed record ProviderTemplateGroupView(string Key, IReadOnlyList<ProviderTemplateItem> Items);

public static class ProviderTemplateItems
{
    public const string CustomId = "custom";
    public const string FeaturedGroup = "featured";
    public const string OtherGroup = "other";
    public const string ResultsGroup = "results";

    /// <summary>
    /// 网格的分组(纯函数):没有搜索词时先「常用」,再按 <see cref="ProviderTemplateGroups.All"/> 的顺序,认不出的分组放「其他」,「自定义」总在最后;
    /// 有搜索词时只有一组「结果」(按名字、id、地址、端点标签匹配,不分大小写),「自定义」不参与搜索。
    /// </summary>
    public static IReadOnlyList<ProviderTemplateGroupView> Group(IReadOnlyList<ProviderTemplateDto> templates, string? query, Func<string, string>? variantText = null)
    {
        var q = query?.Trim() ?? "";
        if (q.Length > 0)
        {
            var hits = templates.Where(t => Matches(t, q, variantText)).Select(t => new ProviderTemplateItem(t)).ToList();
            return hits.Count == 0 ? [] : [new ProviderTemplateGroupView(ResultsGroup, hits)];
        }

        var groups = new List<ProviderTemplateGroupView>();
        var featured = templates.Where(t => t.Featured).Select(t => new ProviderTemplateItem(t)).ToList();
        if (featured.Count > 0)
        {
            groups.Add(new ProviderTemplateGroupView(FeaturedGroup, featured));
        }

        foreach (var g in ProviderTemplateGroups.All)
        {
            var items = templates.Where(t => t.Group == g && !t.Featured).Select(t => new ProviderTemplateItem(t)).ToList();
            if (items.Count > 0)
            {
                groups.Add(new ProviderTemplateGroupView(g, items));
            }
        }

        var other = templates.Where(t => !ProviderTemplateGroups.All.Contains(t.Group) && !t.Featured).Select(t => new ProviderTemplateItem(t)).ToList();
        other.Add(new ProviderTemplateItem(null));
        groups.Add(new ProviderTemplateGroupView(OtherGroup, other));
        return groups;
    }

    private static bool Matches(ProviderTemplateDto t, string q, Func<string, string>? variantText) =>
        t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || t.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
        || t.BaseUrl.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (t.Variant is { } v && variantText is not null && variantText(v).Contains(q, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 「添加 / 编辑供应商」弹窗(docs/design/07 §3「添加供应商」):新建时先选模版(分组网格 + 搜索),再填 key,地址、协议、名字、id 都按模版填好、收在「高级」里;
/// 「自定义」直接展开全部字段。保存 = 写供应商 → 存 key(只写不读,送出就清空)→ 测一次连接;测试失败时原因留在弹窗里,可以改了再试,或「保留并关闭」。
/// 模版是打开弹窗时查到的一次性结果(不进 Store);弹窗的全部状态都是这一次的纯 UI 状态。
/// </summary>
public sealed partial class ProviderEditorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Protocols = ["anthropic", "openai-chat", "openai-responses"];

    private readonly IProviderEditorCommands _commands;
    private readonly string _deviceId;
    private readonly IReadOnlyList<ProviderTemplateDto> _templates;
    private readonly IReadOnlyCollection<string> _existingIds;
    private readonly Func<string, string>? _variantText;

    /// <summary>这次弹窗里已经建出来的供应商(保存过一次,之后再保存是更新,id 锁住)。</summary>
    private string? _createdId;

    /// <summary>新建。</summary>
    /// <param name="variantText">端点标签的本地化文字(搜索用);没有界面的测试传 null。</param>
    /// <param name="discoverable">这台设备上可以导入的候选数(&gt; 0 时弹窗顶部有一行提示)。</param>
    public ProviderEditorViewModel(IProviderEditorCommands commands, string deviceId, IReadOnlyList<ProviderTemplateDto> templates, IReadOnlyCollection<string> existingIds,
        int discoverable = 0, Func<string, string>? variantText = null)
    {
        _commands = commands;
        _deviceId = deviceId;
        _templates = templates;
        _existingIds = existingIds;
        _variantText = variantText;
        IsNew = true;
        DiscoverableCount = discoverable;
        // 设备没给模版(老设备):直接进「自定义」表单
        if (templates.Count == 0)
        {
            Apply(new ProviderTemplateItem(null));
            Step = ProviderEditorStep.Form;
            CanGoBack = false;
        }
        else
        {
            Step = ProviderEditorStep.Pick;
            CanGoBack = true;
            RebuildGroups();
        }
    }

    /// <summary>编辑已有的供应商:直接进表单,id 只读;认得出模版的话带上 logo 和「获取 key」。</summary>
    public ProviderEditorViewModel(IProviderEditorCommands commands, string deviceId, ProviderDto provider, ProviderTemplateDto? template)
    {
        _commands = commands;
        _deviceId = deviceId;
        _templates = [];
        _existingIds = [];
        _filling = true;
        IsNew = false;
        Step = ProviderEditorStep.Form;
        Template = new ProviderTemplateItem(template);
        HasCredential = provider.HasCredential;
        Id = provider.Id;
        Name = provider.Name;
        Protocol = provider.Protocol;
        BaseUrl = provider.BaseUrl;
        Priority = provider.Priority;
        Enabled = provider.Enabled;
        _protocolOptionsJson = provider.ProtocolOptionsJson;
        ShowAdvanced = true; // 编辑多半就是来改这些的
        _filling = false;
    }

    private readonly string? _protocolOptionsJson;

    /// <summary>
    /// 用户在表单里动过(填了 key、改了字段)。界面据此决定点遮罩关不关窗口(防误触丢掉输入);
    /// 选模版时程序填进去的值不算。
    /// </summary>
    [ObservableProperty] public partial bool IsDirty { get; private set; }

    /// <summary>程序正在填表(选模版 / 编辑初始化),这期间的字段变化不算用户改动。</summary>
    private bool _filling;

    partial void OnApiKeyChanged(string value) => Touch();

    partial void OnNameChanged(string value) => Touch();

    partial void OnIdChanged(string value) => Touch();

    partial void OnProtocolChanged(string value) => Touch();

    partial void OnPriorityChanged(int value) => Touch();

    partial void OnEnabledChanged(bool value) => Touch();

    private void Touch()
    {
        if (!_filling)
        {
            IsDirty = true;
        }
    }

    /// <summary>保存完成(含「保留并关闭」),界面据此关窗口。</summary>
    public event Action? Completed;

    /// <summary>点了顶部的「去导入」:界面关窗口,模型页切到「从其他应用导入」。</summary>
    public event Action? DiscoveryRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickStep), nameof(IsFormStep), nameof(ShowsDiscoveryBanner))]
    public partial ProviderEditorStep Step { get; private set; }

    public bool IsPickStep => Step == ProviderEditorStep.Pick;

    public bool IsFormStep => Step == ProviderEditorStep.Form;

    public bool IsNew { get; }

    /// <summary>表单那一步有「返回」(新建且设备给了模版)。</summary>
    public bool CanGoBack { get; }

    public bool ShowsBack => IsNew && CanGoBack;

    // ── 第一步:选模版 ──

    public ObservableCollection<ProviderTemplateGroupView> Groups { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";

    public bool HasNoResults => IsPickStep && Groups.Count == 0;

    partial void OnQueryChanged(string value) => RebuildGroups();

    private void RebuildGroups()
    {
        Groups.Clear();
        foreach (var g in ProviderTemplateItems.Group(_templates, Query, _variantText))
        {
            Groups.Add(g);
        }

        OnPropertyChanged(nameof(HasNoResults));
    }

    public int DiscoverableCount { get; }

    /// <summary>第一步顶部的「在这台设备上找到 N 个已配置的供应商 · 去导入」。</summary>
    public bool ShowsDiscoveryBanner => IsNew && IsPickStep && DiscoverableCount > 0;

    [RelayCommand]
    private void Pick(ProviderTemplateItem? item)
    {
        if (item is null || !IsNew)
        {
            return;
        }

        Apply(item);
        Step = ProviderEditorStep.Form;
    }

    [RelayCommand]
    private void PickCustom() => Pick(new ProviderTemplateItem(null));

    [RelayCommand]
    private void Back()
    {
        if (ShowsBack && _createdId is null)
        {
            ApiKey = "";
            ErrorKey = null;
            TestFailed = false;
            IsDirty = false;
            Step = ProviderEditorStep.Pick;
        }
    }

    [RelayCommand]
    private void GoToDiscovery() => DiscoveryRequested?.Invoke();

    private void Apply(ProviderTemplateItem item)
    {
        _filling = true;
        Template = item;
        ErrorKey = null;
        TestFailed = false;
        ApiKey = "";
        if (item.Dto is { } t)
        {
            Id = ProviderTemplateMatch.SuggestId(t.Id, _existingIds);
            Name = t.Name;
            Protocol = t.Protocol;
            BaseUrl = t.BaseUrl;
            ShowAdvanced = false;
        }
        else
        {
            Id = "";
            Name = "";
            Protocol = "openai-chat";
            BaseUrl = "";
            ShowAdvanced = true;
        }

        Priority = 50;
        Enabled = true;
        _filling = false;
        IsDirty = false;
    }

    // ── 第二步:表单 ──

    /// <summary>当前模版(新建选的那个;编辑时是认出来的那个,认不出为「自定义」)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom), nameof(NeedsKey), nameof(KeyUrl), nameof(HasKeyUrl), nameof(IsLocalService), nameof(CanCollapseAdvanced))]
    public partial ProviderTemplateItem? Template { get; private set; }

    public bool IsCustom => Template?.IsCustom ?? true;

    /// <summary>这家要 API Key(本机服务不要)。</summary>
    public bool NeedsKey => Template?.NeedsKey ?? true;

    /// <summary>本机服务:不画 key 输入框,换成一句「确认它在设备那台电脑上运行」。</summary>
    public bool IsLocalService => !NeedsKey;

    public string? KeyUrl => Template?.Dto?.KeyUrl;

    public bool HasKeyUrl => KeyUrl is not null;

    /// <summary>编辑时:设备上已经有 key(留空 = 不改)。</summary>
    public bool HasCredential { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string Id { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string Name { get; set; } = "";

    [ObservableProperty] public partial string Protocol { get; set; } = "openai-chat";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string BaseUrl { get; set; } = "";

    [ObservableProperty] public partial int Priority { get; set; } = 50;

    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    /// <summary>API Key 输入(只写不读)。编辑时留空 = 不改动已有的 key。</summary>
    [ObservableProperty] public partial string ApiKey { get; set; } = "";

    /// <summary>「高级」(名字、id、地址、协议、启用)展开着。自定义和编辑一直展开。</summary>
    [ObservableProperty] public partial bool ShowAdvanced { get; set; }

    /// <summary>「高级」可以折起来(选了模版的新建)。</summary>
    public bool CanCollapseAdvanced => IsNew && !IsCustom;

    /// <summary>折起来的「高级」那一行右边的摘要:协议 · 主机名。</summary>
    public string AdvancedSummary => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : BaseUrl;

    partial void OnBaseUrlChanged(string value)
    {
        OnPropertyChanged(nameof(AdvancedSummary));
        Touch();
    }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    /// <summary>id 只有新建、还没建出来时能改。</summary>
    public bool CanEditId => IsNew && _createdId is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; private set; }

    /// <summary>保存前 / 写入失败的原因(稳定 key,界面翻译)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorKey { get; private set; }

    public bool HasError => ErrorKey is not null;

    /// <summary>已保存,但连接测试没过(原因在 <see cref="TestErrorKey"/>):弹窗不关,可以改了再试,或「保留并关闭」。</summary>
    [ObservableProperty] public partial bool TestFailed { get; private set; }

    [ObservableProperty] public partial string? TestErrorKey { get; private set; }

    /// <summary>保存成功的供应商 id(弹窗关了以后模型页选中它)。</summary>
    public string? SavedProviderId { get; private set; }

    public bool CanSave => !IsSaving && Name.Trim().Length > 0 && BaseUrl.Trim().Length > 0 && Id.Trim().Length > 0;

    /// <summary>客户端自己发现的问题(不经设备)。</summary>
    public static class Errors
    {
        public const string KeyRequired = "providers.editor.keyRequired";
        public const string IdTaken = "providers.editor.idTaken";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        var id = Id.Trim();
        var key = ApiKey.Trim();
        if (IsNew && _createdId is null && _existingIds.Contains(id))
        {
            ErrorKey = Errors.IdTaken;
            return;
        }

        // 新建一家要 key 的模版:不填 key 存下来也连不上,先拦住(自定义不拦:有的网关不要 key)
        if (key.Length == 0 && NeedsKey && !IsCustom && !HasCredential && _createdId is null)
        {
            ErrorKey = Errors.KeyRequired;
            return;
        }

        IsSaving = true;
        ErrorKey = null;
        TestFailed = false;
        TestErrorKey = null;
        try
        {
            var upsert = await _commands.UpsertAsync(_deviceId, new ProvidersUpsertParams(id, Name.Trim(), Protocol, BaseUrl.Trim(), Priority, Enabled, _protocolOptionsJson));
            if (!upsert.IsOk)
            {
                ErrorKey = upsert.ErrorKey ?? upsert.Outcome.ToString();
                return;
            }

            if (IsNew && _createdId is null)
            {
                _createdId = id;
                OnPropertyChanged(nameof(CanEditId));
            }

            SavedProviderId = id;

            // 密钥只写不读:送出去就清掉输入,之后界面只显示后四位
            ApiKey = "";
            if (key.Length > 0)
            {
                var set = await _commands.SetApiKeyAsync(_deviceId, id, key);
                if (!set.IsOk)
                {
                    ErrorKey = set.ErrorKey ?? set.Outcome.ToString();
                    return;
                }

                HasCredential = true;
            }

            // 测一次连接(用模型列表端点探,不烧 token);没过就把原因留在弹窗里
            var test = await _commands.TestAsync(_deviceId, id);
            if (test is { IsOk: true, Value.Ok: true })
            {
                Completed?.Invoke();
                return;
            }

            TestErrorKey = test.IsOk ? test.Value?.ErrorKey ?? "providers.fetchFailed" : test.ErrorKey ?? test.Outcome.ToString();
            TestFailed = true;
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>测试没过,但用户要保留(例如设备暂时连不上那家):供应商已经存好了,直接关。</summary>
    [RelayCommand]
    private void Keep()
    {
        if (SavedProviderId is not null)
        {
            Completed?.Invoke();
        }
    }
}
