using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Tests.Management;

// 插件设置页的脚本化后端(同时被 Ncw.UI.Tests 以链接方式引用,截图用)。
// 和设备一样:变更方法返回变更之后的完整状态,并合进 Store 的镜像(同一个折叠函数);页面只读 Store。

public sealed class FakePlugins(ClientStores stores) : IPluginsCommands
{
    private long _rev = 1;

    public List<PluginInfoDto> Plugins { get; } = [];

    public List<PluginInteractionDto> Interactions { get; } = [];

    /// <summary>每个调用一行(「方法:参数」),测试据此断言「没发请求」。</summary>
    public List<string> Calls { get; } = [];

    public List<PluginActivityDto> ActivityEntries { get; } = [];

    public List<PluginMarketItemDto> MarketItems { get; } = [];

    public string[] Categories { get; set; } = ["docs", "tools"];

    public bool MarketHasNext { get; set; }

    public PluginUpdateDto[] UpdateList { get; set; } = [];

    /// <summary>设备没有账户令牌:市场安装得到 <c>plugins.authRequired</c>(浏览照常)。</summary>
    public bool AuthRequired { get; set; }

    public ActionResult<PluginsTopicState>? NextListFailure { get; set; }

    public ActionResult<PluginMarketDetailDto>? NextDetailFailure { get; set; }

    public ActionResult<PluginsMarketListResult>? NextMarketFailure { get; set; }

    public ActionResult<PluginsActivityResult>? NextActivityFailure { get; set; }

    public string? CommandFailureKey { get; set; }

    /// <summary>回答提示时让这条请求「已经不存在」(别的控制端先回答了)。</summary>
    public bool ReplyGone { get; set; }

    public Dictionary<string, Dictionary<string, PluginConfigValueDto>> Config { get; } = [];

    public int ListCount => Calls.Count(c => c.StartsWith("list", StringComparison.Ordinal));

    public PluginsTopicState State() => new(_rev, "0.3.3", [.. Plugins], [.. Interactions]);

    /// <summary>推一份快照进 Store(模拟 plugins 主题)。</summary>
    public void Push()
    {
        _rev++;
        var state = State();
        stores.Plugins.Update(m => PluginsReducer.ApplyTopic(m, ManagementRig.Device, state));
    }

    private ActionResult<PluginsTopicState> Mutated()
    {
        Push();
        return R.Ok(State());
    }

    private void Replace(string id, Func<PluginInfoDto, PluginInfoDto> change)
    {
        var i = Plugins.FindIndex(p => p.Id == id);
        if (i >= 0)
        {
            Plugins[i] = change(Plugins[i]);
        }
    }

    // ───────────────────────── 构造夹具 ─────────────────────────

    public static PluginInfoDto Info(
        string id, string name, string status = PluginStatuses.Active, bool enabled = true, string[]? required = null, string[]? optional = null, string[]? granted = null,
        string[]? allowedCommands = null, string[]? hostPermissions = null, PluginConfigurationDto? config = null, PluginCommandDto[]? commands = null,
        PluginDiagnosticDto[]? diagnostics = null, string version = "1.0.0", string? slug = null, string[]? registeredCommands = null, string[]? registeredTools = null,
        PluginMessagesDto[]? messages = null, string description = "A plugin.")
    {
        required ??= [];
        granted ??= required;
        var pending = status == PluginStatuses.PendingApproval ? required.Where(r => !granted.Contains(r)).ToArray() : [];
        return new PluginInfoDto(
            id, name.ToLowerInvariant(), "acme", "extension", name, description, version, "^0.3.0", ["Other"], status, enabled, "/data/plugins/" + id,
            required, optional ?? [], granted, pending, hostPermissions ?? [], allowedCommands ?? [], commands ?? [], [], [], [], [], [], config, [], diagnostics ?? [],
            registeredTools ?? [], registeredCommands ?? (commands ?? []).Select(c => c.Command).ToArray(), messages ?? [], 1_700_000_000_000, 1_700_000_100_000, slug);
    }

    public static PluginDiagnosticDto Diag(string key, string message, string level = "warn", string path = "main.js") => new(path, key, message, level, null);

    public static PluginInteractionDto Exec(string requestId, string pluginId, string command, string environment = "local", string name = "本机", string type = "local", string deviceId = ManagementRig.Device) =>
        new(requestId, pluginId, PluginInteractionKindNames.Exec, null, null, null, [], false, true, null, [], command, new PluginEnvironmentDto(deviceId, environment, name, type), 1_700_000_000_000);

    public static PluginInteractionDto Confirm(string requestId, string pluginId, string titleKey, string? detailKey = null, bool danger = false) =>
        new(requestId, pluginId, PluginInteractionKindNames.Confirm, titleKey, detailKey, null, [], false, danger, null, [], null, null, 1_700_000_001_000);

    public static PluginInteractionDto Input(string requestId, string pluginId, string titleKey, string placeholderKey, string? initial = null) =>
        new(requestId, pluginId, PluginInteractionKindNames.Input, titleKey, null, placeholderKey, [], false, false, initial, [], null, null, 1_700_000_002_000);

    public static PluginInteractionDto Pick(string requestId, string pluginId, string placeholderKey, params (string Id, string Label)[] items) =>
        new(requestId, pluginId, PluginInteractionKindNames.Pick, null, null, placeholderKey, [.. items.Select(i => new PluginPickItemDto(i.Id, i.Label))], false, false, null, [], null, null, 1_700_000_003_000);

    public static PluginInteractionDto PermissionRequest(string requestId, string pluginId, string reasonKey, params string[] permissions) =>
        new(requestId, pluginId, PluginInteractionKindNames.Permissions, reasonKey, null, null, [], false, false, null, permissions, null, null, 1_700_000_004_000);

    public static PluginMarketItemDto MarketItem(string slug, string name, string[]? permissions = null, string version = "1.0.0", string[]? skills = null, string pluginId = "") =>
        new(pluginId.Length > 0 ? pluginId : "acme." + slug, slug, "acme", name, name + " for NextCoWork.", "tools", null, 1200, version, "^0.3.0", permissions ?? [], skills ?? [], "Acme");

    // ───────────────────────── IPluginsCommands ─────────────────────────

    public Task<ActionResult<PluginsTopicState>> ListAsync(string deviceId, CancellationToken ct = default)
    {
        Calls.Add("list");
        if (NextListFailure is { } failure)
        {
            NextListFailure = null;
            return Task.FromResult(failure);
        }

        Push();
        return Task.FromResult(R.Ok(State()));
    }

    public Task<ActionResult<PluginsTopicState>> SetEnabledAsync(string deviceId, string pluginId, bool enabled, CancellationToken ct = default)
    {
        Calls.Add($"setEnabled:{pluginId}:{enabled}");
        var plugin = Plugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin is null)
        {
            return Task.FromResult(R.Fail<PluginsTopicState>(ActionOutcome.NotFound, "plugins.notFound"));
        }

        if (enabled && plugin.Status == PluginStatuses.Error)
        {
            return Task.FromResult(R.Fail<PluginsTopicState>(ActionOutcome.Failed, "plugins.notRunnable"));
        }

        Replace(pluginId, p =>
        {
            if (!enabled)
            {
                return p with { Enabled = false, Status = PluginStatuses.Disabled };
            }

            var missing = p.Required.Where(r => !p.Granted.Contains(r)).ToArray();
            return missing.Length > 0
                ? p with { Enabled = true, Status = PluginStatuses.PendingApproval, PendingPermissions = missing }
                : p with { Enabled = true, Status = PluginStatuses.Active, PendingPermissions = [] };
        });
        return Task.FromResult(Mutated());
    }

    public Task<ActionResult<PluginsTopicState>> UninstallAsync(string deviceId, string pluginId, CancellationToken ct = default)
    {
        Calls.Add($"uninstall:{pluginId}");
        Plugins.RemoveAll(p => p.Id == pluginId);
        return Task.FromResult(Mutated());
    }

    public Task<ActionResult<PluginsInstallResult>> InstallPackageAsync(string deviceId, byte[] zip, string? fileName, CancellationToken ct = default)
    {
        Calls.Add($"installPackage:{fileName}:{zip.Length}");
        var installed = Info("acme.fresh", "Fresh", PluginStatuses.Disabled, enabled: false, required: ["storage"], optional: ["process"], granted: [], allowedCommands: ["echo"]);
        Plugins.RemoveAll(p => p.Id == installed.Id);
        Plugins.Add(installed);
        Push();
        return Task.FromResult(R.Ok(new PluginsInstallResult(installed, State())));
    }

    public Task<ActionResult<PluginsInstallResult>> InstallMarketAsync(string deviceId, string slug, string? version, CancellationToken ct = default)
    {
        Calls.Add($"installMarket:{slug}:{version}");
        if (AuthRequired)
        {
            return Task.FromResult(R.Fail<PluginsInstallResult>(ActionOutcome.Forbidden, "plugins.authRequired"));
        }

        var item = MarketItems.FirstOrDefault(i => i.Slug == slug);
        var installed = Info(item?.PluginId ?? "acme." + slug, item?.DisplayName ?? slug, PluginStatuses.Disabled, enabled: false, required: item?.Permissions ?? [], granted: [], slug: slug);
        Plugins.RemoveAll(p => p.Id == installed.Id);
        Plugins.Add(installed);
        Push();
        return Task.FromResult(R.Ok(new PluginsInstallResult(installed, State())));
    }

    public Task<ActionResult<PluginsTopicState>> GrantAsync(string deviceId, string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default)
    {
        Calls.Add($"grant:{pluginId}:{string.Join(',', permissions)}");
        Replace(pluginId, p =>
        {
            var granted = p.Granted.Concat(permissions.Where(x => p.Required.Contains(x) || p.Optional.Contains(x))).Distinct().ToArray();
            var pending = p.Required.Where(r => !granted.Contains(r)).ToArray();
            var status = p.Status == PluginStatuses.PendingApproval && pending.Length == 0 ? PluginStatuses.Active : p.Status;
            return p with { Granted = granted, PendingPermissions = status == PluginStatuses.PendingApproval ? pending : [], Status = status };
        });
        return Task.FromResult(Mutated());
    }

    public Task<ActionResult<PluginsTopicState>> RevokeAsync(string deviceId, string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default)
    {
        Calls.Add($"revoke:{pluginId}:{string.Join(',', permissions)}");
        Replace(pluginId, p =>
        {
            var granted = p.Granted.Where(g => !permissions.Contains(g)).ToArray();
            var missingRequired = p.Required.Any(r => !granted.Contains(r));
            return missingRequired && p.Status is not (PluginStatuses.Error or PluginStatuses.Disabled)
                ? p with { Granted = granted, Enabled = false, Status = PluginStatuses.Disabled } // 撤掉必选能力 = 插件停下来
                : p with { Granted = granted };
        });
        return Task.FromResult(Mutated());
    }

    public Task<ActionResult<PluginsActivityResult>> ActivityAsync(string deviceId, string? pluginId, CancellationToken ct = default)
    {
        Calls.Add($"activity:{pluginId}");
        if (NextActivityFailure is { } failure)
        {
            NextActivityFailure = null;
            return Task.FromResult(failure);
        }

        return Task.FromResult(R.Ok(new PluginsActivityResult([.. ActivityEntries.Where(e => pluginId is null || e.PluginId == pluginId)])));
    }

    public Task<ActionResult<Unit>> RunCommandAsync(string deviceId, string pluginId, string commandId, string? workspaceId = null, string? argsJson = null, CancellationToken ct = default)
    {
        Calls.Add($"runCommand:{pluginId}:{commandId}:{workspaceId}");
        return Task.FromResult(CommandFailureKey is { } key ? R.Fail<Unit>(ActionOutcome.Failed, key) : R.Ok(Unit.Value));
    }

    public Task<ActionResult<PluginsCardActionResult>> CardActionAsync(string deviceId, string pluginId, string callId, string actionId, string? valueJson = null, CancellationToken ct = default)
    {
        Calls.Add($"cardAction:{pluginId}:{callId}:{actionId}");
        return Task.FromResult(R.Ok(new PluginsCardActionResult(true)));
    }

    public Task<ActionResult<Unit>> ReplyInteractionAsync(string deviceId, string requestId, bool? confirmed = null, string? text = null, string? pickedId = null, CancellationToken ct = default)
    {
        Calls.Add($"reply:{requestId}:{confirmed}:{text}:{pickedId}");
        if (ReplyGone)
        {
            Interactions.RemoveAll(i => i.RequestId == requestId); // 别的控制端先回答了:设备上已经没有它
            return Task.FromResult(R.Fail<Unit>(ActionOutcome.NotFound, "plugins.interactionNotFound"));
        }

        Interactions.RemoveAll(i => i.RequestId == requestId);
        Push();
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<ActionResult<PluginsMarketListResult>> MarketListAsync(string deviceId, string? query, string? category, int page = 1, CancellationToken ct = default)
    {
        Calls.Add($"marketList:{query}:{category}:{page}");
        if (NextMarketFailure is { } failure)
        {
            NextMarketFailure = null;
            return Task.FromResult(failure);
        }

        var items = MarketItems.Where(i => (query is null || i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) && (category is null || i.Category == category)).ToArray();
        return Task.FromResult(R.Ok(new PluginsMarketListResult(items, MarketHasNext, page)));
    }

    public Task<ActionResult<PluginsMarketCategoriesResult>> MarketCategoriesAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new PluginsMarketCategoriesResult(Categories)));

    public Task<ActionResult<PluginMarketDetailDto>> MarketDetailAsync(string deviceId, string slug, CancellationToken ct = default)
    {
        Calls.Add($"marketDetail:{slug}");
        if (NextDetailFailure is { } failure)
        {
            NextDetailFailure = null;
            return Task.FromResult(failure);
        }

        var item = MarketItems.First(i => i.Slug == slug);
        return Task.FromResult(R.Ok(new PluginMarketDetailDto(item, [new PluginMarketVersionDto("1.1.0", "More tools.", true, 20_000), new PluginMarketVersionDto("1.0.0", "First release.", null, 18_000)])));
    }

    public Task<ActionResult<PluginsUpdatesResult>> CheckUpdatesAsync(string deviceId, bool force = false, CancellationToken ct = default)
    {
        Calls.Add($"checkUpdates:{force}");
        return Task.FromResult(R.Ok(new PluginsUpdatesResult(UpdateList)));
    }

    public Task<ActionResult<PluginsUpdateAllResult>> UpdateAllAsync(string deviceId, CancellationToken ct = default)
    {
        Calls.Add("updateAll");
        var updated = UpdateList.Where(u => u.FromMarket).Select(u => u.PluginId).ToArray();
        foreach (var id in updated)
        {
            Replace(id, p => p with { Version = UpdateList.First(u => u.PluginId == id).LatestVersion });
        }

        UpdateList = [.. UpdateList.Where(u => !u.FromMarket)];
        Push();
        return Task.FromResult(R.Ok(new PluginsUpdateAllResult(updated, [])));
    }

    public Task<ActionResult<PluginsConfigurationResult>> GetConfigurationAsync(string deviceId, string pluginId, CancellationToken ct = default)
    {
        Calls.Add($"getConfig:{pluginId}");
        return Task.FromResult(R.Ok(new PluginsConfigurationResult(pluginId, Values(pluginId))));
    }

    private Dictionary<string, PluginConfigValueDto> Values(string pluginId)
    {
        var plugin = Plugins.First(p => p.Id == pluginId);
        var values = (plugin.Configuration?.Properties ?? []).ToDictionary(p => p.Key, p => new PluginConfigValueDto(p.DefaultText, p.DefaultNumber, p.DefaultFlag));
        if (Config.TryGetValue(pluginId, out var set))
        {
            foreach (var (key, value) in set)
            {
                values[key] = value;
            }
        }

        return values;
    }

    public Task<ActionResult<PluginsConfigurationResult>> SetConfigurationAsync(string deviceId, string pluginId, string key, PluginConfigValueDto? value, CancellationToken ct = default)
    {
        Calls.Add($"setConfig:{pluginId}:{key}:{value?.Text}:{value?.Number}:{value?.Flag}");
        var property = Plugins.First(p => p.Id == pluginId).Configuration?.Properties.FirstOrDefault(p => p.Key == key);
        if (property is null)
        {
            return Task.FromResult(R.Fail<PluginsConfigurationResult>(ActionOutcome.Invalid, "plugins.configurationInvalid"));
        }

        if (value is not null)
        {
            var fits = property.Type switch
            {
                "boolean" => value.Flag is not null,
                "number" => value.Number is not null,
                "enum" => value.Text is not null && property.Enum.Contains(value.Text),
                _ => value.Text is not null,
            };
            if (!fits)
            {
                return Task.FromResult(R.Fail<PluginsConfigurationResult>(ActionOutcome.Invalid, "plugins.configurationInvalid"));
            }
        }

        if (!Config.TryGetValue(pluginId, out var set))
        {
            Config[pluginId] = set = [];
        }

        if (value is null)
        {
            set.Remove(key);
        }
        else
        {
            set[key] = value;
        }

        return Task.FromResult(R.Ok(new PluginsConfigurationResult(pluginId, Values(pluginId))));
    }
}
