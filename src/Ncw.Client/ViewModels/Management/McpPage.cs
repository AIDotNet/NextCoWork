using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>服务器连接状态的显示(纯函数 <see cref="McpStatus.Of"/>)。设备断开 = <see cref="Unknown"/>(不显示旧的「已连接」,也不显示「N 个工具」)。</summary>
public enum McpConnection
{
    Unknown,
    Disabled,
    Connecting,
    Connected,
    Failed,
}

public static class McpStatus
{
    public static McpConnection Of(bool known, string state) => !known
        ? McpConnection.Unknown
        : state switch
        {
            McpStates.Connected => McpConnection.Connected,
            McpStates.Connecting => McpConnection.Connecting,
            McpStates.Failed => McpConnection.Failed,
            _ => McpConnection.Disabled,
        };
}

/// <summary>列表里的一台 MCP 服务器。配置与状态合成一行(设备合成的,没有中间帧);密钥值不存在于这里,只有键名。</summary>
public sealed partial class McpServerRowViewModel(McpViewModel owner, McpServerStatusDto dto, bool known) : ObservableObject
{
    public McpServerStatusDto Dto { get; } = dto;

    public string Id => Dto.Config.Id;

    public string Name => Dto.Config.Name;

    public string Transport => Dto.Config.Transport;

    public bool IsStdio => Dto.Config.Transport == McpTransports.Stdio;

    public bool IsEnabled => Dto.Config.Enabled;

    public string? Description => Dto.Config.Description;

    public bool IsProjectBound => Dto.Config.WorkspaceId is not null;

    public McpConnection Connection { get; } = McpStatus.Of(known, dto.State);

    /// <summary>连接细节(启动命令 / 地址),一行文本。</summary>
    public string Target => IsStdio ? string.Join(' ', new[] { Dto.Config.Command }.Concat(Dto.Config.Args ?? [])) : Dto.Config.Url ?? "";

    public string? ErrorKey => Connection == McpConnection.Failed ? Dto.ErrorKey : null;

    /// <summary>连接成功才有意义:未知 / 断开时不显示工具数。</summary>
    public int ToolCount => Connection == McpConnection.Connected ? Dto.Tools.Length : 0;

    public bool ShowsTools => Connection == McpConnection.Connected;

    public int RejectedCount => Connection == McpConnection.Connected ? Dto.Rejected.Length : 0;

    public string? ServerInfo => Connection == McpConnection.Connected && Dto.ServerName is { } n ? $"{n} {Dto.ServerVersion}".Trim() : null;

    public int Attempt => Dto.Attempt;

    /// <summary>
    /// 卡片上只留一个和状态有关的主操作(docs/design/03 §11.6):连接失败 / 连接中 → 「重连」;连上了 → 「资源与提示」;其余(测试、编辑、删除)在「⋯」菜单里。
    /// 原来五个按钮一字排开、每张卡片都一样,删除和编辑长得一样。
    /// </summary>
    public bool PrimaryIsReconnect => Connection is McpConnection.Failed or McpConnection.Connecting;

    public bool PrimaryIsBrowse => Connection == McpConnection.Connected;

    [RelayCommand]
    private Task ToggleAsync() => owner.SetEnabledAsync(this, !IsEnabled);

    [RelayCommand]
    private Task ReconnectAsync() => owner.ReconnectAsync(this);

    [RelayCommand]
    private Task EditAsync() => owner.EditAsync(this);

    [RelayCommand]
    private Task TestAsync() => owner.TestSavedAsync(this);

    [RelayCommand]
    private Task BrowseAsync() => owner.BrowseAsync(this);

    [RelayCommand]
    private void AskRemove() => owner.AskRemove(this);
}

/// <summary>编辑器里的一个密钥键(环境变量 / 请求头):值只写不读。</summary>
public sealed partial class McpSecretRowViewModel(string name, bool isSet) : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = name;

    [ObservableProperty] public partial string Value { get; set; } = "";

    /// <summary>设备上已经存了这个键的值(界面只显示「已设置」)。</summary>
    public bool IsSet { get; } = isSet;
}

public enum McpProblem
{
    Id,
    Name,
    Command,
    Url,
    UrlCredentials,
    SecretName,
    SecretsIncomplete,
}

/// <summary>新建 / 编辑 MCP 服务器的表单(stdio 或 HTTP)。校验和设备一致(<c>McpConfigRules</c>),不合法的字段列在 <see cref="Problems"/>。</summary>
public sealed partial class McpEditorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Transports = [McpTransports.Stdio, McpTransports.StreamableHttp, McpTransports.Sse];

    private static readonly Regex IdPattern = new("^[a-zA-Z0-9_-]{1,32}$", RegexOptions.Compiled);
    private static readonly Regex EnvPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex HeaderPattern = new("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}$", RegexOptions.Compiled);

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial bool IsNew { get; set; } = true;

    [ObservableProperty] public partial string Id { get; set; } = "";

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty] public partial string Transport { get; set; } = McpTransports.Stdio;

    [ObservableProperty] public partial string Description { get; set; } = "";

    [ObservableProperty] public partial string Command { get; set; } = "";

    /// <summary>参数,一行一个。</summary>
    [ObservableProperty] public partial string Args { get; set; } = "";

    [ObservableProperty] public partial string Cwd { get; set; } = "";

    [ObservableProperty] public partial string Url { get; set; } = "";

    /// <summary>绑定到当前项目(服务器在项目的执行环境里运行,只对这个项目可见);否则全局。</summary>
    [ObservableProperty] public partial bool BindToProject { get; set; }

    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    public ObservableCollection<McpSecretRowViewModel> Secrets { get; } = [];

    public McpEditorViewModel() => Secrets.CollectionChanged += (_, e) =>
    {
        foreach (var row in e.NewItems?.OfType<McpSecretRowViewModel>() ?? [])
        {
            row.PropertyChanged += (_, _) => Touch(); // 改键名 / 填值要立刻重新校验
        }

        Touch();
    };

    [ObservableProperty] public partial Failure? SaveFailure { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }

    public bool IsStdio => Transport == McpTransports.Stdio;

    partial void OnTransportChanged(string value) => Touch();

    partial void OnIdChanged(string value) => Touch();

    partial void OnNameChanged(string value) => Touch();

    partial void OnCommandChanged(string value) => Touch();

    partial void OnUrlChanged(string value) => Touch();

    internal void Touch()
    {
        OnPropertyChanged(nameof(IsStdio));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(CanSave));
    }

    public IReadOnlyList<McpProblem> Problems
    {
        get
        {
            var problems = new List<McpProblem>();
            if (IsNew && !IdPattern.IsMatch(Id))
            {
                problems.Add(McpProblem.Id);
            }

            if (Name.Trim().Length is 0 or > 120)
            {
                problems.Add(McpProblem.Name);
            }

            if (IsStdio)
            {
                if (Command.Trim().Length == 0)
                {
                    problems.Add(McpProblem.Command);
                }
            }
            else if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                problems.Add(McpProblem.Url);
            }
            else if (uri.UserInfo.Length > 0)
            {
                problems.Add(McpProblem.UrlCredentials); // 凭据应放进请求头,不能明文躺在地址里
            }

            var pattern = IsStdio ? EnvPattern : HeaderPattern;
            if (Secrets.Any(s => !pattern.IsMatch(s.Name)) || Secrets.Select(s => s.Name).Distinct().Count() != Secrets.Count)
            {
                problems.Add(McpProblem.SecretName);
            }

            // 写密钥是「整张 map 一次写完」:输入了任何一个值,就必须把所有键的值都给齐(旧值取不回来,漏掉的会被丢弃)
            if (Secrets.Any(s => s.Value.Length > 0) && Secrets.Any(s => s.Value.Length == 0))
            {
                problems.Add(McpProblem.SecretsIncomplete);
            }

            return problems;
        }
    }

    public bool CanSave => !IsSaving && Problems.Count == 0;

    public bool WritesSecrets => Secrets.Count > 0 && Secrets.All(s => s.Value.Length > 0);

    public McpServerConfigDto ToConfig(string? workspaceId, bool keepEnabled = true) => new(
        Id.Trim(),
        Name.Trim(),
        Transport,
        keepEnabled && Enabled,
        Description.Trim().Length == 0 ? null : Description.Trim(),
        BindToProject ? workspaceId : null,
        IsStdio ? Command.Trim() : null,
        IsStdio ? Args.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null,
        IsStdio ? [.. Secrets.Select(s => s.Name)] : null,
        IsStdio && Cwd.Trim().Length > 0 ? Cwd.Trim() : null,
        IsStdio ? null : Url.Trim(),
        IsStdio ? null : [.. Secrets.Select(s => s.Name)]);

    internal void AddSecret()
    {
        Secrets.Add(new McpSecretRowViewModel("", false));
        Touch();
    }

    internal void RemoveSecret(McpSecretRowViewModel row)
    {
        Secrets.Remove(row);
        Touch();
    }

    internal void ClearSecretValues()
    {
        foreach (var s in Secrets)
        {
            s.Value = "";
        }
    }
}

/// <summary>
/// 设置 · 设备 · MCP:服务器列表与连接状态(<see cref="ClientStores.Mcp"/> 的镜像,<c>mcp</c> 主题全量权威)、新建 / 编辑(stdio / HTTP)、启停、重连、测试连接、删除(二次确认)、
/// 资源与提示浏览。密钥只写不读(只显示键名与「已设置」),写密钥是整张 map 一次写完。读取 member 起,写入 owner(会在设备上起进程 / 连网)。设备断开时状态是「未知」。
/// </summary>
public sealed partial class McpViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly IMcpCommands _commands;

    public McpViewModel(SettingsScope scope, ClientStores stores, IMcpCommands commands) : base(scope, Need.Member)
    {
        _stores = stores;
        _commands = commands;
        stores.Mcp.Changed += Sync;
        stores.Workspace.Changed += RaiseGates;
    }

    public ObservableCollection<McpServerRowViewModel> Servers { get; } = [];

    public McpEditorViewModel Editor { get; } = new();

    public bool HasServers => Servers.Count > 0;

    public bool ShowsEmpty => IsReady && Servers.Count == 0;

    [ObservableProperty] public partial PendingConfirm? PendingRemove { get; private set; }

    public bool HasPendingRemove => PendingRemove is not null;

    partial void OnPendingRemoveChanged(PendingConfirm? value) => OnPropertyChanged(nameof(HasPendingRemove));

    // ── 测试连接(一次性结果,纯 UI 状态) ──
    [ObservableProperty] public partial McpTestResultDto? TestResult { get; private set; }

    [ObservableProperty] public partial bool IsTesting { get; private set; }

    // ── 资源与提示浏览 ──
    [ObservableProperty] public partial McpServerRowViewModel? Selected { get; private set; }

    [ObservableProperty] public partial bool BrowseLoading { get; private set; }

    [ObservableProperty] public partial Failure? BrowseFailure { get; private set; }

    public ObservableCollection<McpResourceDto> Resources { get; } = [];

    public ObservableCollection<McpPromptDto> Prompts { get; } = [];

    protected override Task LoadAsync()
    {
        // 状态经 mcp 主题全量推送(member 起);镜像到了就就绪,没到继续「加载中」
        Sync();
        return Task.CompletedTask;
    }

    protected override void OnUnavailable()
    {
        // 规则 7:设备断开不丢掉列表,但每一行标成「未知」(不显示「已连接 / N 个工具」)
        Rebuild(known: false);
        Editor.IsOpen = false;
        PendingRemove = null;
    }

    private void Sync()
    {
        if (Scope.DeviceId is not { } device || !Scope.IsConnected || !ReadGate.Allowed)
        {
            return;
        }

        var mirror = _stores.Mcp.State.Of(device);
        if (!mirror.Known)
        {
            if (State == PageState.Ready)
            {
                State = PageState.Loading;
            }

            return;
        }

        Rebuild(known: true);
        if (State != PageState.Ready)
        {
            Ready();
        }
    }

    private void Rebuild(bool known)
    {
        var servers = Scope.DeviceId is { } device ? _stores.Mcp.State.Of(device).Servers : [];
        var rows = servers.Select(s => new McpServerRowViewModel(this, s, known)).ToList();
        if (!rows.Select(r => (r.Dto, r.Connection)).SequenceEqual(Servers.Select(r => (r.Dto, r.Connection))))
        {
            Servers.Clear();
            foreach (var row in rows)
            {
                Servers.Add(row);
            }
        }

        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(ShowsEmpty));
    }

    // ───────────────────────── 服务器操作 ─────────────────────────

    internal async Task SetEnabledAsync(McpServerRowViewModel row, bool enabled)
    {
        if (Scope.DeviceId is { } device && CanWrite)
        {
            _ = await Act(_commands.SetEnabledAsync(device, row.Id, enabled));
        }
    }

    internal async Task ReconnectAsync(McpServerRowViewModel row)
    {
        if (Scope.DeviceId is { } device && CanWrite)
        {
            _ = await Act(_commands.ReconnectAsync(device, row.Id));
        }
    }

    internal void AskRemove(McpServerRowViewModel row) => PendingRemove = CanWrite ? new PendingConfirm(row.Name, row.Id) : null;

    [RelayCommand]
    private void CancelRemove() => PendingRemove = null;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        if (PendingRemove is not { } pending || Scope.DeviceId is not { } device || !CanWrite)
        {
            return;
        }

        PendingRemove = null;
        _ = await Act(_commands.RemoveAsync(device, pending.Id));
        if (Selected?.Id == pending.Id)
        {
            Selected = null;
        }
    }

    internal async Task TestSavedAsync(McpServerRowViewModel row)
    {
        if (Scope.DeviceId is not { } device || !CanWrite)
        {
            return;
        }

        IsTesting = true;
        TestResult = null;
        try
        {
            var result = await Act(_commands.TestConnectionAsync(device, row.Id));
            TestResult = result.IsOk ? result.Value : null;
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ───────────────────────── 编辑器 ─────────────────────────

    [RelayCommand]
    private void New()
    {
        if (!CanWrite)
        {
            return;
        }

        var e = Editor;
        e.IsNew = true;
        e.Id = "";
        e.Name = "";
        e.Transport = McpTransports.Stdio;
        e.Description = "";
        e.Command = "";
        e.Args = "";
        e.Cwd = "";
        e.Url = "";
        e.BindToProject = false;
        e.Enabled = true;
        e.Secrets.Clear();
        e.SaveFailure = null;
        TestResult = null;
        e.IsOpen = true;
        e.Touch();
    }

    internal async Task EditAsync(McpServerRowViewModel row)
    {
        if (!CanWrite || Scope.DeviceId is not { } device)
        {
            return;
        }

        var (e, c) = (Editor, row.Dto.Config);
        e.IsNew = false;
        e.Id = c.Id;
        e.Name = c.Name;
        e.Transport = c.Transport;
        e.Description = c.Description ?? "";
        e.Command = c.Command ?? "";
        e.Args = string.Join('\n', c.Args ?? []);
        e.Cwd = c.Cwd ?? "";
        e.Url = c.Url ?? "";
        e.BindToProject = c.WorkspaceId is not null;
        e.Enabled = c.Enabled;
        e.Secrets.Clear();
        e.SaveFailure = null;
        TestResult = null;
        e.IsOpen = true;

        // 键名来自配置;「是否已设置」单次取回(只有键名和布尔,没有值)
        var info = await _commands.GetSecretsInfoAsync(device, c.Id);
        var set = info.IsOk ? info.Value!.Entries.ToDictionary(x => x.Name, x => x.IsSet, StringComparer.Ordinal) : [];
        foreach (var name in (c.Transport == McpTransports.Stdio ? c.EnvNames : c.HeaderNames) ?? [])
        {
            e.Secrets.Add(new McpSecretRowViewModel(name, set.GetValueOrDefault(name)));
        }

        e.Touch();
    }

    [RelayCommand]
    private void CloseEditor()
    {
        Editor.ClearSecretValues();
        Editor.IsOpen = false;
    }

    [RelayCommand]
    private void AddSecret() => Editor.AddSecret();

    [RelayCommand]
    private void RemoveSecret(McpSecretRowViewModel row) => Editor.RemoveSecret(row);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite || !Editor.CanSave)
        {
            return;
        }

        if (Editor.BindToProject && Scope.WorkspaceId is null)
        {
            Editor.SaveFailure = new Failure(ActionOutcome.Invalid, "mcp.workspaceInvalid");
            return;
        }

        Editor.IsSaving = true;
        Editor.SaveFailure = null;
        try
        {
            var config = Editor.ToConfig(Scope.WorkspaceId);
            var saved = await _commands.UpsertAsync(device, config);
            if (!saved.IsOk)
            {
                Editor.SaveFailure = Failure.Of(saved);
                return;
            }

            if (Editor.WritesSecrets)
            {
                var values = Editor.Secrets.ToDictionary(s => s.Name, s => s.Value, StringComparer.Ordinal);
                var set = await _commands.SetSecretsAsync(device, config.Id, values);
                Editor.ClearSecretValues(); // 值送出去就清掉
                if (!set.IsOk)
                {
                    Editor.SaveFailure = Failure.Of(set);
                    return;
                }
            }

            Editor.IsOpen = false;
        }
        finally
        {
            Editor.IsSaving = false;
            Editor.Touch();
        }
    }

    /// <summary>测试编辑器里这份草稿(还没保存;密钥仍取同 id 已存的那份)。</summary>
    [RelayCommand]
    private async Task TestDraftAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite || Editor.Problems.Any(p => p != McpProblem.SecretsIncomplete))
        {
            return;
        }

        IsTesting = true;
        TestResult = null;
        try
        {
            var config = Editor.ToConfig(Scope.WorkspaceId);
            var result = await _commands.TestConnectionAsync(device, config.Id, config);
            if (result.IsOk)
            {
                TestResult = result.Value;
            }
            else
            {
                Editor.SaveFailure = Failure.Of(result);
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ───────────────────────── 资源与提示 ─────────────────────────

    internal async Task BrowseAsync(McpServerRowViewModel row)
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        Selected = row;
        BrowseFailure = null;
        Resources.Clear();
        Prompts.Clear();
        if (row.Connection != McpConnection.Connected)
        {
            BrowseFailure = new Failure(ActionOutcome.Unavailable, "mcp.notConnected"); // 没连上就不去问
            return;
        }

        BrowseLoading = true;
        try
        {
            var resources = await _commands.ListResourcesAsync(device, row.Id);
            var prompts = await _commands.ListPromptsAsync(device, row.Id);
            if (Selected?.Id != row.Id)
            {
                return;
            }

            if (resources.IsOk)
            {
                foreach (var r in resources.Value!.Resources)
                {
                    Resources.Add(r);
                }
            }

            if (prompts.IsOk)
            {
                foreach (var p in prompts.Value!.Prompts)
                {
                    Prompts.Add(p);
                }
            }

            BrowseFailure = !resources.IsOk ? Failure.Of(resources) : !prompts.IsOk ? Failure.Of(prompts) : null;
        }
        finally
        {
            BrowseLoading = false;
        }
    }

    [RelayCommand]
    private void CloseBrowse() => Selected = null;
}
