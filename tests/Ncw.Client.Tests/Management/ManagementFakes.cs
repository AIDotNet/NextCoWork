using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Tests.Management;

// 设置 / 管理页的脚本化后端(同时被 Ncw.UI.Tests 以链接方式引用,截图用)。
// 和真实的 *Actions 一样:读取 / 变更的结果合进 Store(镜像由设备推送,这里模拟),页面只读 Store。

public static class R
{
    public static ActionResult<T> Ok<T>(T value) => new(ActionOutcome.Ok, value);

    public static ActionResult<T> Fail<T>(ActionOutcome outcome, string? key = null) => new(outcome, default, key);
}

/// <summary>一台已连接的本机设备 + 一个项目的 Store,可以改角色 / 断开设备。</summary>
public sealed class ManagementRig
{
    public const string Device = "local";
    public const string Workspace = "ws1";

    public ManagementRig(string role = "owner")
    {
        Stores = new ClientStores();
        Stores.Workspace.Update(_ => WorkspaceState.Empty with
        {
            Devices = ImmutableDictionary<string, DeviceInfo>.Empty.Add(Device, new DeviceInfo(Device, "本机", DeviceKind.Local, DeviceConnection.Connected, null, Role: role)),
            Projects = ImmutableDictionary<ProjectRef, ProjectInfo>.Empty
                .Add(new ProjectRef(Device, Workspace), new ProjectInfo(new ProjectRef(Device, Workspace), "NextCoWork", "/Users/me/NextCoWork", EnvironmentKind.Local, null, "main", true))
                .Add(new ProjectRef(Device, "ws2"), new ProjectInfo(new ProjectRef(Device, "ws2"), "Octopus", "/Users/me/Octopus", EnvironmentKind.Local, null, "main", true)),
        });
        Scope = new SettingsScope(Stores);
    }

    public ClientStores Stores { get; }

    public SettingsScope Scope { get; }

    public void SetRole(string? role) => UpdateDevice(d => d with { Role = role });

    public void SetConnection(DeviceConnection connection) => UpdateDevice(d => d with { Connection = connection });

    /// <summary>把设备标成远程设备(通道不是本机:危险操作、密钥不可用)。</summary>
    public void MakeRemote() => UpdateDevice(d => d with { Kind = DeviceKind.Remote });

    private void UpdateDevice(Func<DeviceInfo, DeviceInfo> change) =>
        Stores.Workspace.Update(s => s with { Devices = s.Devices.SetItem(Device, change(s.Devices[Device])) });

    public ProviderOptions Options => new(Stores);

    public static ModelOptionDto Option(string provider, string alias, string name) =>
        new(provider, provider, alias, name, alias, 200_000, 8192, true, true, true, true, null, null, "anthropic");

    /// <summary>往 providers 镜像里放模型候选(辅助模型下拉用)。</summary>
    public void SeedModels(params ModelOptionDto[] options) =>
        Stores.Providers.Update(m => m with
        {
            Devices = m.Devices.SetItem(Device, m.Of(Device) with { Known = true, Options = [.. options] }),
        });
}

public readonly struct ProviderOptions(ClientStores stores)
{
    public ClientStores Stores { get; } = stores;
}

public sealed class ScriptedDialogs : IDialogService
{
    public PickedFile? NextFile { get; set; }

    public Stream? NextSave { get; set; }

    public List<FilePickKind> Picks { get; } = [];

    public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => Task.FromResult<string?>(null);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>("/Backups");

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

    public Task<PickedFile?> PickFileAsync(FilePickKind kind)
    {
        Picks.Add(kind);
        return Task.FromResult(NextFile);
    }

    public Task<Stream?> PickSaveStreamAsync(FilePickKind kind, string suggestedName) => Task.FromResult(NextSave);

    public static PickedFile File(string name, long size, string? path = null, byte[]? bytes = null) =>
        new(name, path, size, () => Task.FromResult<Stream>(new MemoryStream(bytes ?? new byte[Math.Min(size, 64)])));
}

public sealed class FakeLauncher : IUrlLauncher
{
    public List<string> Opened { get; } = [];

    public Task OpenAsync(string url)
    {
        Opened.Add(url);
        return Task.CompletedTask;
    }
}

public sealed class FakeSettings(ClientStores stores) : ISettingsCommands
{
    public List<DeviceSettingsPatchDto> Patches { get; } = [];

    public List<ProjectSettingsPatchDto> ProjectPatches { get; } = [];

    public List<string> Passwords { get; } = [];

    public ActionResult<DeviceSettingsState>? NextFailure { get; set; }

    public ProjectSettingsDto Project { get; set; } = new("ws1", "ask", new ModelPickDto("sonnet", "anthropic"), "code", "auto", true, false, null, null);

    public Func<string, ActionResult<ProjectSettingsDto>>? ProjectOverride { get; set; }

    public static DeviceSettingsDto Defaults() => new(
        new ModelPickDto("sonnet", "anthropic"), ModelPickDto.None, ModelPickDto.None, ModelPickDto.None, "inherit", ModelPickDto.None, ModelPickDto.None, ModelPickDto.None,
        "ask", "auto", true, 32_000, true, false, false, false, "system", 30, 600, true, false, "",
        new SubagentSettingsDto(ModelPickDto.None, "inherit", 3, 5),
        new ProxySettingsDto(false, "system", "http", "", 0, false, "", "", false),
        new GatewaySettingsDto(false, false, 19836),
        new DataSettingsDto(null, "manual"),
        new PersonalizationSettingsDto("", "", ""));

    /// <summary>推一份设置快照进 Store(模拟 settings 主题)。</summary>
    public void Push(DeviceSettingsDto settings, long rev = 1) =>
        stores.Settings.Update(m => ServicesReducer.ApplySettings(m, ManagementRig.Device, new DeviceSettingsState(rev, settings)));

    public Task<ActionResult<DeviceSettingsState>> UpdateAsync(string deviceId, DeviceSettingsPatchDto patch, CancellationToken ct = default)
    {
        Patches.Add(patch);
        if (NextFailure is { } failure)
        {
            NextFailure = null;
            return Task.FromResult(failure);
        }

        var current = stores.Settings.State.Of(deviceId);
        var s = current.Settings ?? Defaults();
        s = s with
        {
            AccountRotation = patch.AccountRotation ?? s.AccountRotation,
            Failover = patch.Failover ?? s.Failover,
            AutoCompact = patch.AutoCompact ?? s.AutoCompact,
            DefaultPermissionMode = patch.DefaultPermissionMode ?? s.DefaultPermissionMode,
            CompactThinking = patch.CompactThinking ?? s.CompactThinking,
            GoalCheckinMinutes = patch.GoalCheckinMinutes ?? s.GoalCheckinMinutes,
            UpstreamIdleTimeoutSeconds = patch.UpstreamIdleTimeoutSeconds ?? s.UpstreamIdleTimeoutSeconds,
            MaxOutputTokens = patch.MaxOutputTokens ?? s.MaxOutputTokens,
            PermissionReviewer = patch.PermissionReviewer ?? s.PermissionReviewer,
            Gateway = patch.Gateway is { } g ? s.Gateway with { AllowRemote = g.AllowRemote ?? s.Gateway.AllowRemote, Enabled = g.Enabled ?? s.Gateway.Enabled } : s.Gateway,
            Data = patch.Data is { } d ? s.Data with { BackupDirectory = d.BackupDirectory ?? s.Data.BackupDirectory, BackupFrequency = d.BackupFrequency ?? s.Data.BackupFrequency } : s.Data,
            Proxy = patch.Proxy is { } p ? s.Proxy with { Enabled = p.Enabled ?? s.Proxy.Enabled, Host = p.Host ?? s.Proxy.Host, Port = p.Port ?? s.Proxy.Port, Mode = p.Mode ?? s.Proxy.Mode } : s.Proxy,
            Personalization = patch.Personalization is { } per ? new PersonalizationSettingsDto(per.Name ?? s.Personalization.Name, per.Background ?? s.Personalization.Background, per.Instructions ?? s.Personalization.Instructions) : s.Personalization,
        };
        var state = new DeviceSettingsState(current.Rev + 1, s);
        stores.Settings.Update(m => ServicesReducer.ApplySettings(m, deviceId, state));
        return Task.FromResult(R.Ok(state));
    }

    public Task<ActionResult<ProjectSettingsDto>> GetProjectAsync(string deviceId, string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(ProjectOverride?.Invoke(workspaceId) ?? R.Ok(Project with { WorkspaceId = workspaceId }));

    public Task<ActionResult<ProjectSettingsDto>> UpdateProjectAsync(string deviceId, ProjectSettingsPatchDto patch, CancellationToken ct = default)
    {
        ProjectPatches.Add(patch);
        Project = Project with
        {
            PermissionMode = patch.PermissionMode ?? Project.PermissionMode,
            DefaultMode = patch.DefaultMode ?? Project.DefaultMode,
            DefaultThinking = patch.DefaultThinking ?? Project.DefaultThinking,
            WebSearch = patch.WebSearch ?? Project.WebSearch,
            MaxContext = patch.MaxContext ?? Project.MaxContext,
            DefaultModel = patch.DefaultModel ?? Project.DefaultModel,
        };
        return Task.FromResult(R.Ok(Project));
    }

    public Task<ActionResult<DeviceSettingsState>> SetProxyPasswordAsync(string deviceId, string password, CancellationToken ct = default)
    {
        Passwords.Add(password);
        return Task.FromResult(R.Ok(new DeviceSettingsState(99, stores.Settings.State.Of(deviceId).Settings ?? Defaults())));
    }
}

public sealed class FakeSkills(ClientStores stores) : ISkillsCommands
{
    private long _seq;

    public List<SkillInfoDto> Skills { get; } = [];

    public SkillDiagnosticDto[] Diagnostics { get; set; } = [];

    public SkillMarketItemDto[] Market { get; set; } = [];

    public ActionResult<SkillsListResult>? ListFailure { get; set; }

    public ActionResult<SkillsMarketListResult>? MarketFailure { get; set; }

    public bool AuthRequired { get; set; }

    public List<(string Slug, string Scope)> MarketInstalls { get; } = [];

    public List<(int Length, string? Name, string Scope)> ZipInstalls { get; } = [];

    public List<string> Uninstalled { get; } = [];

    public List<string> MarketQueries { get; } = [];

    public Func<string?, Task>? MarketGate { get; set; }

    public static SkillInfoDto Skill(string id, string name, string scope = "global", string source = "folder", bool global = true, bool active = true, bool assets = false) =>
        new(id, name, name + " description", "dev", source, scope, null, global, active, assets, "1.0.0", null, "/skills/" + id, 3, 0);

    private void Merge(string? workspace)
    {
        foreach (var held in stores.Skills.State.WorkspacesOf(ManagementRig.Device).Append(workspace).Distinct().ToList())
        {
            stores.Skills.Update(m => SkillsReducer.ApplyList(m, ManagementRig.Device, held, new SkillsListResult([.. Skills]), ++_seq));
        }
    }

    public Task<ActionResult<SkillsListResult>> ListAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        if (ListFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        Merge(workspaceId);
        return Task.FromResult(R.Ok(new SkillsListResult([.. Skills])));
    }

    public Task<ActionResult<SkillsDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        stores.Skills.Update(m => SkillsReducer.ApplyDiagnostics(m, deviceId, workspaceId, new SkillsDiagnosticsResult(Diagnostics), ++_seq));
        return Task.FromResult(R.Ok(new SkillsDiagnosticsResult(Diagnostics)));
    }

    public Task<ActionResult<SkillsListResult>> SetGlobalEnabledAsync(string deviceId, string skillId, bool enabled, string? workspaceId = null, CancellationToken ct = default)
    {
        Skills[Skills.FindIndex(s => s.Id == skillId)] = Skills.First(s => s.Id == skillId) with { GlobalEnabled = enabled };
        Merge(workspaceId);
        return Task.FromResult(R.Ok(new SkillsListResult([.. Skills])));
    }

    public Task<ActionResult<SkillsListResult>> SetWorkspaceActiveAsync(string deviceId, string workspaceId, string skillId, bool active, CancellationToken ct = default)
    {
        Skills[Skills.FindIndex(s => s.Id == skillId)] = Skills.First(s => s.Id == skillId) with { ActiveInWorkspace = active };
        Merge(workspaceId);
        return Task.FromResult(R.Ok(new SkillsListResult([.. Skills])));
    }

    public Task<ActionResult<SkillsInstallResult>> InstallZipAsync(string deviceId, byte[] zip, string? fileName, string scope, string? workspaceId = null, CancellationToken ct = default)
    {
        ZipInstalls.Add((zip.Length, fileName, scope));
        var installed = Skill("zip-" + ZipInstalls.Count, fileName ?? "zip", scope == "project" ? "project" : "global", "zip");
        Skills.Add(installed);
        Merge(workspaceId);
        return Task.FromResult(R.Ok(new SkillsInstallResult(installed, [.. Skills])));
    }

    public Task<ActionResult<SkillsInstallResult>> InstallMarketAsync(string deviceId, string slug, string? version, string scope, string? workspaceId = null, CancellationToken ct = default)
    {
        if (AuthRequired)
        {
            return Task.FromResult(R.Fail<SkillsInstallResult>(ActionOutcome.Failed, "skills.authRequired"));
        }

        MarketInstalls.Add((slug, scope));
        var installed = Skill("market-" + slug, slug);
        Skills.Add(installed);
        Merge(workspaceId);
        return Task.FromResult(R.Ok(new SkillsInstallResult(installed, [.. Skills])));
    }

    public Task<ActionResult<SkillsListResult>> UninstallAsync(string deviceId, string skillId, string? scope = null, string? workspaceId = null, CancellationToken ct = default)
    {
        Uninstalled.Add(skillId);
        Skills.RemoveAll(s => s.Id == skillId);
        Merge(workspaceId);
        return Task.FromResult(R.Ok(new SkillsListResult([.. Skills])));
    }

    public async Task<ActionResult<SkillsMarketListResult>> MarketListAsync(string deviceId, string? query, string? category, CancellationToken ct = default)
    {
        MarketQueries.Add(query ?? "");
        if (MarketGate is { } gate)
        {
            await gate(query);
        }

        return MarketFailure ?? R.Ok(new SkillsMarketListResult([.. Market.Where(m => query is null || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))]));
    }

    public Task<ActionResult<SkillsMarketCategoriesResult>> MarketCategoriesAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new SkillsMarketCategoriesResult(["dev", "writing"])));

    public Task<ActionResult<SkillMarketDetailDto>> MarketDetailAsync(string deviceId, string slug, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new SkillMarketDetailDto(Market.First(m => m.Slug == slug), [new SkillMarketVersionDto("1.2.0", "Fixes", null, 1024)])));
}

public sealed class FakeCommandsAdmin(ClientStores stores) : ICommandsAdminCommands
{
    private long _seq;

    public List<CommandItemDto> All { get; } = [];

    public CommandDiagnosticDto[] Diagnostics { get; set; } = [];

    public ActionResult<CommandsListAllResult>? Failure { get; set; }

    public Task<ActionResult<CommandsListAllResult>> ListAllAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult(failure);
        }

        var result = new CommandsListAllResult([.. All]);
        stores.Commands.Update(m => CommandsReducer.ApplyListAll(m, deviceId, workspaceId, result, ++_seq));
        return Task.FromResult(R.Ok(result));
    }

    public Task<ActionResult<CommandsDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var result = new CommandsDiagnosticsResult(Diagnostics);
        stores.Commands.Update(m => CommandsReducer.ApplyDiagnostics(m, deviceId, workspaceId, result, ++_seq));
        return Task.FromResult(R.Ok(result));
    }

    public Task<ActionResult<CommandsListAllResult>> SetEnabledAsync(string deviceId, string name, bool enabled, string? workspaceId = null, CancellationToken ct = default)
    {
        All[All.FindIndex(c => c.Name == name)] = All.First(c => c.Name == name) with { Enabled = enabled };
        return ListAllAsync(deviceId, workspaceId, ct);
    }
}

public sealed class FakeHooks(ClientStores stores) : IHooksCommands
{
    private long _seq;

    public List<HookDto> Hooks { get; } = [];

    public HookDiagnosticDto[] Diagnostics { get; set; } = [];

    public List<(string Scope, string? Workspace, HookUpsertDto Hook)> Saved { get; } = [];

    public List<HookUpsertDto> Tested { get; } = [];

    public List<string> Deleted { get; } = [];

    public ActionResult<HooksSaveResult>? SaveFailure { get; set; }

    public string? Warning { get; set; }

    public HookRunReportDto Report { get; set; } = new("h", "global", 0, "ok\n", "", 42, HookNames.OutcomeOk, HookNames.DecisionAllow);

    public static HookDto Hook(string id, string evt = "PreToolUse", string scope = "global", bool enabled = true, string? matcher = "Bash(git *)", string command = "echo hi", string[]? env = null) =>
        new(id, scope, evt, HookNames.TypeCommand, matcher, enabled, 30_000, "Guard " + id, command, null, null, null, env ?? [], "/settings.json");

    /// <summary>和真实的 HooksActions 一样:全局层的改动让这台设备上持有的每一份清单都重拉。</summary>
    private void Merge(string? workspace)
    {
        foreach (var held in stores.Hooks.State.WorkspacesOf(ManagementRig.Device).Append(workspace).Distinct().ToList())
        {
            stores.Hooks.Update(m => HooksReducer.ApplyList(m, ManagementRig.Device, held, new HooksListResult([.. Hooks]), ++_seq));
        }
    }

    public Task<ActionResult<HooksListResult>> ListAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        Merge(workspaceId);
        return Task.FromResult(R.Ok(new HooksListResult([.. Hooks])));
    }

    public Task<ActionResult<HooksDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var result = new HooksDiagnosticsResult(Diagnostics);
        stores.Hooks.Update(m => HooksReducer.ApplyDiagnostics(m, deviceId, workspaceId, result, ++_seq));
        return Task.FromResult(R.Ok(result));
    }

    public Task<ActionResult<HooksSaveResult>> SaveAsync(string deviceId, string scope, string? workspaceId, HookUpsertDto hook, CancellationToken ct = default)
    {
        Saved.Add((scope, workspaceId, hook));
        if (SaveFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        var dto = Hook(hook.Id ?? "new-" + Saved.Count, hook.Event, scope, hook.Enabled, hook.Matcher, hook.Command ?? "", [.. (hook.Env ?? []).Select(e => e.Name)]);
        var at = Hooks.FindIndex(h => h.Id == dto.Id);
        if (at >= 0)
        {
            Hooks[at] = dto;
        }
        else
        {
            Hooks.Add(dto);
        }

        Merge(workspaceId);
        return Task.FromResult(R.Ok(new HooksSaveResult(dto, Warning)));
    }

    public Task<ActionResult<Unit>> DeleteAsync(string deviceId, string scope, string? workspaceId, string id, CancellationToken ct = default)
    {
        Deleted.Add(id);
        Hooks.RemoveAll(h => h.Id == id);
        Merge(workspaceId);
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<ActionResult<Unit>> SetEnabledAsync(string deviceId, string scope, string? workspaceId, string id, bool enabled, CancellationToken ct = default)
    {
        Hooks[Hooks.FindIndex(h => h.Id == id)] = Hooks.First(h => h.Id == id) with { Enabled = enabled };
        Merge(workspaceId);
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<ActionResult<HookRunReportDto>> TestAsync(string deviceId, string scope, string? workspaceId, HookUpsertDto draft, CancellationToken ct = default)
    {
        Tested.Add(draft);
        return Task.FromResult(R.Ok(Report));
    }
}

public sealed class FakeMcp(ClientStores stores) : IMcpCommands
{
    private long _rev;

    public List<McpServerStatusDto> Servers { get; } = [];

    public List<(string Id, bool Enabled)> Toggled { get; } = [];

    public List<string> Reconnected { get; } = [];

    public List<string> Removed { get; } = [];

    public List<McpServerConfigDto> Upserts { get; } = [];

    public List<(string Id, Dictionary<string, string> Values)> SecretWrites { get; } = [];

    public McpTestResultDto TestResult { get; set; } = new(true, ["read", "write"], 120, null, null, null, "srv", "1.0", "2025-06-18", []);

    public McpSecretEntryDto[] SecretInfo { get; set; } = [];

    public ActionResult<McpResourcesResult>? ResourcesFailure { get; set; }

    public static McpServerStatusDto Server(string id, string state = McpStates.Connected, string transport = McpTransports.Stdio, string? errorKey = null, bool enabled = true) =>
        new(new McpServerConfigDto(id, id.ToUpperInvariant(), transport, enabled, null, null, transport == McpTransports.Stdio ? "npx" : null, transport == McpTransports.Stdio ? ["-y", id] : null,
                transport == McpTransports.Stdio ? ["TOKEN"] : null, null, transport == McpTransports.Stdio ? null : "https://example.com/mcp", transport == McpTransports.Stdio ? null : ["Authorization"]),
            state, errorKey, null, state == McpStates.Connected ? ["read", "write", "search"] : [], [], state == McpStates.Connected ? 1 : null, 0, id, "1.0");

    /// <summary>推一份全量状态进 Store(模拟 mcp 主题)。</summary>
    public void Push() => stores.Mcp.Update(m => McpReducer.ApplyTopic(m, ManagementRig.Device, new McpTopicState(++_rev, [.. Servers])));

    public Task<ActionResult<McpTopicState>> ListAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(R.Ok(new McpTopicState(_rev, [.. Servers])));

    public Task<ActionResult<McpServerStatusDto>> UpsertAsync(string deviceId, McpServerConfigDto config, CancellationToken ct = default)
    {
        Upserts.Add(config);
        var status = Server(config.Id, transport: config.Transport) with { Config = config };
        Servers.RemoveAll(s => s.Config.Id == config.Id);
        Servers.Add(status);
        Push();
        return Task.FromResult(R.Ok(status));
    }

    public Task<ActionResult<McpRemoveResult>> RemoveAsync(string deviceId, string serverId, CancellationToken ct = default)
    {
        Removed.Add(serverId);
        Servers.RemoveAll(s => s.Config.Id == serverId);
        Push();
        return Task.FromResult(R.Ok(new McpRemoveResult(true)));
    }

    public Task<ActionResult<McpServerStatusDto>> SetEnabledAsync(string deviceId, string serverId, bool enabled, CancellationToken ct = default)
    {
        Toggled.Add((serverId, enabled));
        var at = Servers.FindIndex(s => s.Config.Id == serverId);
        Servers[at] = Servers[at] with { Config = Servers[at].Config with { Enabled = enabled }, State = enabled ? McpStates.Connected : McpStates.Disabled };
        Push();
        return Task.FromResult(R.Ok(Servers[at]));
    }

    public Task<ActionResult<McpServerStatusDto>> ReconnectAsync(string deviceId, string serverId, CancellationToken ct = default)
    {
        Reconnected.Add(serverId);
        return Task.FromResult(R.Ok(Servers.First(s => s.Config.Id == serverId)));
    }

    public Task<ActionResult<McpTestResultDto>> TestConnectionAsync(string deviceId, string serverId, McpServerConfigDto? draft = null, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(TestResult));

    public Task<ActionResult<McpSecretsInfoDto>> SetSecretsAsync(string deviceId, string serverId, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        SecretWrites.Add((serverId, new Dictionary<string, string>(values)));
        return Task.FromResult(R.Ok(new McpSecretsInfoDto(serverId, "env", [.. values.Keys.Select(k => new McpSecretEntryDto(k, true))])));
    }

    public Task<ActionResult<McpSecretsInfoDto>> GetSecretsInfoAsync(string deviceId, string serverId, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new McpSecretsInfoDto(serverId, "env", SecretInfo)));

    public Task<ActionResult<McpResourcesResult>> ListResourcesAsync(string deviceId, string serverId, CancellationToken ct = default) =>
        Task.FromResult(ResourcesFailure ?? R.Ok(new McpResourcesResult([new McpResourceDto("file:///notes.md", "notes", "Notes", "text/markdown")])));

    public Task<ActionResult<McpPromptsResult>> ListPromptsAsync(string deviceId, string serverId, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new McpPromptsResult([new McpPromptDto("summarize", "Summarize a file", ["path"])])));
}

public sealed class FakeUsage : IUsageCommands
{
    public UsageSummaryDto Summary { get; set; } = new(10, 9, 1, 1000, 500, 200, 0, 0, 0, 0, 1700, 0.2, 800, 300, 4, 1, [new UsageCostTotalDto("USD", 1_500_000)]);

    public List<UsageDimensionStatDto> Providers { get; } = [];

    public List<UsageDimensionStatDto> Models { get; } = [];

    public List<UsageDailyBucketDto> Buckets { get; } = [];

    public ActionResult<UsageSummaryDto>? SummaryFailure { get; set; }

    public List<(UsageWindowDto Window, string Kind)> Calls { get; } = [];

    public Func<UsageWindowDto, Task>? Gate { get; set; }

    public static UsageDailyBucketDto Bucket(string day, long requests = 3, long input = 100, long output = 50, string currency = "USD", long? cost = 200_000, long priced = -1) =>
        new(day, "anthropic", "Anthropic", "sonnet", "sonnet", currency, requests, requests, input, output, 0, 0, 0, 0, cost, priced < 0 ? requests : priced, 0, 0, 0);

    public async Task<ActionResult<UsageSummaryDto>> GetSummaryAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default)
    {
        Calls.Add((window, "summary"));
        if (Gate is { } gate)
        {
            await gate(window);
        }

        return SummaryFailure ?? R.Ok(Summary);
    }

    public Task<ActionResult<UsageDimensionStatsResult>> GetProviderStatsAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new UsageDimensionStatsResult([.. Providers])));

    public Task<ActionResult<UsageDimensionStatsResult>> GetModelStatsAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new UsageDimensionStatsResult([.. Models])));

    public Task<ActionResult<UsageDailySeriesResult>> GetDailySeriesAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new UsageDailySeriesResult([.. Buckets])));
}

public sealed class FakeStorage : IStorageCommands
{
    public StorageStatsDto Stats { get; set; } = new(40_000_000, 1_000_000, 120_000_000, 300_000_000, 42, 1800, 12, 900, 3, null);

    public BackupStatusDto Backup { get; set; } = new("/Backups", 1_700_000_000_000, "/Backups/ncw.bak", null, false);

    public CleanupPreviewDto Preview { get; set; } = new("age", 5, 120, 3, 4_000_000, [], "tok-1");

    public List<string> Calls { get; } = [];

    public List<(string Kind, string? Age, string Token)> Executed { get; } = [];

    public List<string> Restored { get; } = [];

    public List<string> Applied { get; } = [];

    public List<string> Discarded { get; } = [];

    public ActionResult<CleanupResultDto>? ExecuteFailure { get; set; }

    public ActionResult<StorageStatsDto>? StatsFailure { get; set; }

    public Task<ActionResult<StorageStatsDto>> GetStatsAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(StatsFailure ?? R.Ok(Stats));

    public Task<ActionResult<StorageStatsDto>> VacuumAsync(string deviceId, CancellationToken ct = default)
    {
        Calls.Add("vacuum");
        return Task.FromResult(R.Ok(Stats));
    }

    public Task<ActionResult<BackupStatusDto>> GetBackupStatusAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(R.Ok(Backup));

    public Task<ActionResult<BackupStatusDto>> CreateBackupAsync(string deviceId, bool manual = true, CancellationToken ct = default)
    {
        Calls.Add("backup");
        Backup = Backup with { LastBackupAt = 1_800_000_000_000 };
        return Task.FromResult(R.Ok(Backup));
    }

    public Task<ActionResult<RestorePreviewDto>> PreviewRestoreAsync(string deviceId, string path, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new RestorePreviewDto(path, new BackupManifestDto("3.0.0", 1_700_000_000_000, 5, 40, 900, true), "tok-restore")));

    public Task<ActionResult<BackupStatusDto>> RestoreBackupAsync(string deviceId, string path, string confirmToken, CancellationToken ct = default)
    {
        Restored.Add(path + "|" + confirmToken);
        return Task.FromResult(R.Ok(Backup));
    }

    public Task<ActionResult<CleanupPreviewDto>> PreviewCleanupAsync(string deviceId, string kind, string? age = null, CancellationToken ct = default)
    {
        Calls.Add("preview:" + kind + ":" + age);
        return Task.FromResult(R.Ok(Preview with { Kind = kind }));
    }

    public Task<ActionResult<CleanupResultDto>> CleanupAttachmentsAsync(string deviceId, CancellationToken ct = default)
    {
        Calls.Add("attachments");
        return Task.FromResult(R.Ok(new CleanupResultDto(Preview with { Kind = "attachments", ConfirmToken = null }, 7)));
    }

    public Task<ActionResult<CleanupResultDto>> ExecuteCleanupAsync(string deviceId, string kind, string? age, string confirmToken, CancellationToken ct = default)
    {
        Executed.Add((kind, age, confirmToken));
        return Task.FromResult(ExecuteFailure ?? R.Ok(new CleanupResultDto(Preview, 5)));
    }

    public Task<ActionResult<ExportInfoDto>> ExportToAsync(string deviceId, Stream destination, CancellationToken ct = default)
    {
        Calls.Add("export");
        destination.WriteByte(1);
        return Task.FromResult(R.Ok(new ExportInfoDto("e", 1, 1, 1, 1)));
    }

    public Task<ActionResult<ImportPreviewDto>> ImportFromAsync(string deviceId, Stream source, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new ImportPreviewDto("up-1", 1, 2, 10, 300, 8, 2, 0, "tok-import")));

    public Task<ActionResult<ImportResultDto>> ApplyImportAsync(string deviceId, string uploadId, string confirmToken, CancellationToken ct = default)
    {
        Applied.Add(uploadId + "|" + confirmToken);
        return Task.FromResult(R.Ok(new ImportResultDto(8, 2, 0, 2, 10, 300)));
    }

    public Task<ActionResult<Unit>> DiscardImportAsync(string deviceId, string uploadId, CancellationToken ct = default)
    {
        Discarded.Add(uploadId);
        return Task.FromResult(R.Ok(Unit.Value));
    }
}

public sealed class FakeGateway : IGatewayCommands
{
    public GatewayStatusDto Status { get; set; } = new(false, null, "127.0.0.1", false, false, [], null);

    public string[] Addresses { get; set; } = ["http://127.0.0.1:19836"];

    public List<string> Revealed { get; } = [];

    public List<string> Rotated { get; } = [];

    public List<bool> Enabled { get; } = [];

    public List<string?> Resets { get; } = [];

    public Task<ActionResult<GatewayStatusDto>> GetStatusAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(R.Ok(Status));

    public Task<ActionResult<GatewayStatusDto>> SetEnabledAsync(string deviceId, bool enabled, CancellationToken ct = default)
    {
        Enabled.Add(enabled);
        Status = Status with { Enabled = enabled, Port = enabled ? 19836 : null };
        return Task.FromResult(R.Ok(Status));
    }

    public Task<ActionResult<GatewayKeyDto>> GetKeyAsync(string deviceId, string role, CancellationToken ct = default)
    {
        Revealed.Add(role);
        return Task.FromResult(R.Ok(new GatewayKeyDto(role, "ncw_" + role + "_secret")));
    }

    public Task<ActionResult<GatewayKeyDto>> RotateKeyAsync(string deviceId, string role, CancellationToken ct = default)
    {
        Rotated.Add(role);
        return Task.FromResult(R.Ok(new GatewayKeyDto(role, "ncw_" + role + "_rotated")));
    }

    public Task<ActionResult<GatewayAddressesResult>> GetAddressesAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(R.Ok(new GatewayAddressesResult(Addresses)));

    public Task<ActionResult<GatewayStatusDto>> ResetHealthAsync(string deviceId, string? providerId, CancellationToken ct = default)
    {
        Resets.Add(providerId);
        return Task.FromResult(R.Ok(Status));
    }
}

public sealed class FakeSchedules(ClientStores stores) : ISchedulesCommands
{
    private long _rev;

    public List<ScheduledTaskDto> Tasks { get; } = [];

    public List<ScheduledRunDto> Runs { get; } = [];

    public List<ScheduledRunDto> History { get; } = [];

    public List<ScheduledTaskInputDto> Created { get; } = [];

    public List<(string Id, ScheduledTaskPatchDto Patch)> Updated { get; } = [];

    public List<string> Deleted { get; } = [];

    public List<string> RanNow { get; } = [];

    public List<ScheduledRunsQuery> RunQueries { get; } = [];

    public ActionResult<ScheduledTaskDto>? CreateFailure { get; set; }

    public ActionResult<ScheduledRunDto>? RunNowFailure { get; set; }

    public static ScheduledTaskDto MakeTask(string id, string name, string kind = "daily", string time = "09:00", bool enabled = true, string workspace = ManagementRig.Workspace, long? next = 1_800_000_000_000, int[]? weekdays = null) =>
        new(id, name, "Do the daily thing for " + name, workspace, "sonnet", "anthropic",
            new ScheduleRuleDto(kind, kind == "once" ? "2026-12-01T" + time : null, kind == "once" ? null : time, weekdays), "Asia/Shanghai", new RepeatWindowDto(false), enabled, enabled ? next : null, 1, 1, "user", 0);

    public static ScheduledRunDto Run(string id, string taskId, string status = "success", long at = 1_799_000_000_000, string? error = null, string? summary = "Done: 3 files changed") =>
        new(id, taskId, "session-" + id, "scheduled", status, at, at + 1000, status is "queued" or "running" ? null : at + 61_000, summary, error);

    /// <summary>推一份全量状态进 Store(模拟 schedules 主题)。</summary>
    public void Push() => stores.Schedules.Update(m => ServicesReducer.ApplySchedules(m, ManagementRig.Device, new SchedulesTopicState(++_rev, [.. Tasks], [.. Runs])));

    public Task<ActionResult<ScheduledTaskDto>> CreateAsync(string deviceId, ScheduledTaskInputDto input, CancellationToken ct = default)
    {
        Created.Add(input);
        if (CreateFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        var task = MakeTask("new-" + Created.Count, input.Name, input.Schedule.Kind, input.Schedule.Time ?? "09:00", input.Enabled ?? true, input.WorkspaceId) with { Schedule = input.Schedule, Prompt = input.Prompt };
        Tasks.Add(task);
        Push();
        return Task.FromResult(R.Ok(task));
    }

    public Task<ActionResult<ScheduledTaskDto>> UpdateAsync(string deviceId, string taskId, ScheduledTaskPatchDto patch, CancellationToken ct = default)
    {
        Updated.Add((taskId, patch));
        var at = Tasks.FindIndex(t => t.Id == taskId);
        Tasks[at] = Tasks[at] with { Name = patch.Name ?? Tasks[at].Name, Schedule = patch.Schedule ?? Tasks[at].Schedule, Enabled = patch.Enabled ?? Tasks[at].Enabled };
        Push();
        return Task.FromResult(R.Ok(Tasks[at]));
    }

    public Task<ActionResult<Unit>> DeleteAsync(string deviceId, string taskId, CancellationToken ct = default)
    {
        Deleted.Add(taskId);
        Tasks.RemoveAll(t => t.Id == taskId);
        Push();
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<ActionResult<ScheduledTaskDto>> SetEnabledAsync(string deviceId, string taskId, bool enabled, CancellationToken ct = default)
    {
        var at = Tasks.FindIndex(t => t.Id == taskId);
        Tasks[at] = Tasks[at] with { Enabled = enabled };
        Push();
        return Task.FromResult(R.Ok(Tasks[at]));
    }

    public Task<ActionResult<ScheduledRunDto>> RunNowAsync(string deviceId, string taskId, CancellationToken ct = default)
    {
        RanNow.Add(taskId);
        if (RunNowFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        var run = Run("manual-" + RanNow.Count, taskId, "queued", at: 1_800_000_000_000) with { Trigger = "manual" };
        Runs.Add(run);
        Push();
        return Task.FromResult(R.Ok(run));
    }

    public Task<ActionResult<ScheduledRunsResult>> ListRunsAsync(string deviceId, ScheduledRunsQuery query, CancellationToken ct = default)
    {
        RunQueries.Add(query);
        var page = History.Where(r => r.TaskId == query.TaskId && (query.BeforeScheduledAt is null || r.ScheduledAt < query.BeforeScheduledAt)).OrderByDescending(r => r.ScheduledAt).Take(query.Limit).ToArray();
        return Task.FromResult(R.Ok(new ScheduledRunsResult(page, History.Count(r => r.TaskId == query.TaskId && (query.BeforeScheduledAt is null || r.ScheduledAt < query.BeforeScheduledAt)) > page.Length)));
    }

    public Task<ActionResult<ScheduledRunDto>> GetRunAsync(string deviceId, string runId, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(Runs.Concat(History).First(r => r.Id == runId)));

    public Task<ActionResult<Unit>> DeleteRunAsync(string deviceId, string runId, CancellationToken ct = default)
    {
        History.RemoveAll(r => r.Id == runId);
        Runs.RemoveAll(r => r.Id == runId);
        Push();
        return Task.FromResult(R.Ok(Unit.Value));
    }
}

/// <summary>一次装好整套管理页 ViewModel(单测 / 截图共用)。</summary>
public sealed class ManagementKit
{
    public ManagementKit(string role = "owner", DateTimeOffset? now = null, DateTimeOffset? clockNow = null)
    {
        Rig = new ManagementRig(role);
        Clock = new FixedClock(clockNow ?? now ?? new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8)));
        Settings = new FakeSettings(Rig.Stores);
        Skills = new FakeSkills(Rig.Stores);
        Commands = new FakeCommandsAdmin(Rig.Stores);
        Hooks = new FakeHooks(Rig.Stores);
        Mcp = new FakeMcp(Rig.Stores);
        Usage = new FakeUsage();
        Storage = new FakeStorage();
        Gateway = new FakeGateway();
        Schedules = new FakeSchedules(Rig.Stores);
        Plugins = new FakePlugins(Rig.Stores);
        Dialogs = new ScriptedDialogs();
        Launcher = new FakeLauncher();
        Pages = new ManagementPages(Rig.Stores, Settings, Skills, Commands, Hooks, Mcp, Usage, Storage, Gateway, () => Dialogs, () => Launcher, Clock, Plugins);
        Scheduled = new SchedulesViewModel(Rig.Stores, Schedules, Clock);
    }

    public ManagementRig Rig { get; }

    public FixedClock Clock { get; }

    public FakeSettings Settings { get; }

    public FakeSkills Skills { get; }

    public FakeCommandsAdmin Commands { get; }

    public FakeHooks Hooks { get; }

    public FakeMcp Mcp { get; }

    public FakeUsage Usage { get; }

    public FakeStorage Storage { get; }

    public FakeGateway Gateway { get; }

    public FakeSchedules Schedules { get; }

    public FakePlugins Plugins { get; }

    public ScriptedDialogs Dialogs { get; }

    public FakeLauncher Launcher { get; }

    public ManagementPages Pages { get; }

    public SchedulesViewModel Scheduled { get; }

    public ClientStores Stores => Rig.Stores;
}

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}
