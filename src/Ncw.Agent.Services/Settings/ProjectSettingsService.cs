using System.Text.Json.Nodes;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Settings;

/// <summary>
/// 项目作用域的设置(工作区 JSON 的 <c>settings</c> 段里归 3.0 设置页管的那几项)。<see cref="CompactModel"/> / <see cref="CompactThinking"/>
/// 为 null = 跟随全局设置(2.x 的三态:缺席或空串 = 跟随全局,<c>null</c> = 显式跟随会话模型,非空 = 用这个别名;3.0 把「显式跟随会话模型」折进 <see cref="CompactThinking"/> = <c>inherit</c> / 空别名)。
/// </summary>
public sealed record ProjectSettings(
    string WorkspaceId,
    string PermissionMode,
    ModelPick DefaultModel,
    string DefaultMode,
    string DefaultThinking,
    bool WebSearch,
    bool MaxContext,
    ModelPick? CompactModel,
    string? CompactThinking);

public sealed record ProjectSettingsPatch
{
    public string? PermissionMode { get; init; }
    public ModelPick? DefaultModel { get; init; }
    public string? DefaultMode { get; init; }
    public string? DefaultThinking { get; init; }
    public bool? WebSearch { get; init; }
    public bool? MaxContext { get; init; }
    public ModelPick? CompactModel { get; init; }
    public string? CompactThinking { get; init; }
    public bool ClearCompactModel { get; init; }
    public bool ClearCompactThinking { get; init; }
}

/// <summary>读写项目设置。校验规则和设备设置一样(整个补丁先校验,坏值不许落库);读是宽容的(坏值按出厂值)。</summary>
public sealed class ProjectSettingsService(WorkspaceRepository workspaces)
{
    /// <summary>项目不存在(或属于别的作用域)返回 null。</summary>
    public ProjectSettings? Get(string workspaceId) => workspaces.Get(workspaceId) is { } w ? Read(w) : null;

    /// <summary>改项目设置;项目不存在返回 null。不合法抛 <see cref="SettingsException"/>。</summary>
    public ProjectSettings? Update(string workspaceId, ProjectSettingsPatch patch)
    {
        Validate(patch);
        var updated = workspaces.UpdateSettings(workspaceId, settings =>
        {
            Set(settings, "permissionMode", patch.PermissionMode);
            if (patch.DefaultModel is { } model)
            {
                settings["defaultModel"] = model.Model;
                if (model.IsSet && !string.IsNullOrEmpty(model.ProviderId))
                {
                    settings["defaultModelProviderId"] = model.ProviderId;
                }
                else
                {
                    settings.Remove("defaultModelProviderId");
                }
            }

            Set(settings, "defaultMode", patch.DefaultMode);
            Set(settings, "defaultThinking", patch.DefaultThinking);
            if (patch.WebSearch is { } web)
            {
                settings["webSearch"] = web;
            }

            if (patch.MaxContext is { } max)
            {
                settings["maxContext"] = max;
            }

            if (patch.ClearCompactModel)
            {
                // 2.x 浅合并清不掉旧值,「改回跟随全局」写空串;3.0 直接删键(读的一侧缺席和空串同义)。
                settings.Remove("compactModel");
                settings.Remove("compactModelProviderId");
            }
            else if (patch.CompactModel is { } compact)
            {
                settings["compactModel"] = compact.Model;
                if (compact.IsSet && !string.IsNullOrEmpty(compact.ProviderId))
                {
                    settings["compactModelProviderId"] = compact.ProviderId;
                }
                else
                {
                    settings.Remove("compactModelProviderId");
                }
            }

            if (patch.ClearCompactThinking)
            {
                settings.Remove("compactThinking");
            }
            else
            {
                Set(settings, "compactThinking", patch.CompactThinking);
            }
        });
        return updated is null ? null : Read(updated);
    }

    private static ProjectSettings Read(WorkspaceRecord w)
    {
        var s = w.Json["settings"] as JsonObject;
        var compactModel = DeviceSettings.Str(s, "compactModel") is { Length: > 0 } cm
            ? new ModelPick(cm, DeviceSettings.Str(s, "compactModelProviderId") is { Length: > 0 } cp ? cp : null)
            : null;
        var thinking = DeviceSettings.Str(s, "compactThinking");
        return new ProjectSettings(
            w.Id,
            OneOf(DeviceSettings.Str(s, "permissionMode"), DeviceSettings.PermissionModes, "ask"),
            DeviceSettings.Str(s, "defaultModel") is { Length: > 0 } dm
                ? new ModelPick(dm, DeviceSettings.Str(s, "defaultModelProviderId") is { Length: > 0 } dp ? dp : null)
                : ModelPick.None,
            DeviceSettings.Str(s, "defaultMode") is { Length: > 0 } mode ? mode : "code",
            OneOf(DeviceSettings.Str(s, "defaultThinking"), DeviceSettings.ThinkingLevels, "auto"),
            DeviceSettings.Bool(s, "webSearch") ?? true,
            DeviceSettings.Bool(s, "maxContext") ?? false,
            compactModel,
            thinking is not null && (thinking == DeviceSettings.Inherit || DeviceSettings.ThinkingLevels.Contains(thinking)) ? thinking : null);
    }

    private static void Validate(ProjectSettingsPatch p)
    {
        Choice("permissionMode", p.PermissionMode, DeviceSettings.PermissionModes);
        if (p.DefaultMode is { } mode && (mode.Length is 0 or > 64 || !(char.IsAsciiLetterLower(mode[0]) || char.IsAsciiDigit(mode[0])) || mode.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))))
        {
            throw new SettingsException("settings.invalid.choice", "defaultMode", $"'{mode}' is not a valid session mode id.");
        }

        Choice("defaultThinking", p.DefaultThinking, DeviceSettings.ThinkingLevels);
        Choice("compactThinking", p.CompactThinking, [DeviceSettings.Inherit, .. DeviceSettings.ThinkingLevels]);
        foreach (var (field, pick) in new (string, ModelPick?)[] { ("defaultModel", p.DefaultModel), ("compactModel", p.CompactModel) })
        {
            if (pick is not null && (pick.Model.Length > 256 || (pick.ProviderId?.Length ?? 0) > 256 || pick.Model.Trim() != pick.Model))
            {
                throw new SettingsException("settings.invalid.model", field, $"'{field}' is not a valid model reference.");
            }
        }
    }

    private static void Choice(string field, string? value, IReadOnlyList<string> allowed)
    {
        if (value is not null && !allowed.Contains(value))
        {
            throw new SettingsException("settings.invalid.choice", field, $"'{value}' is not an allowed value for '{field}'.");
        }
    }

    private static string OneOf(string? value, IReadOnlyList<string> allowed, string fallback) =>
        value is not null && allowed.Contains(value) ? value : fallback;

    private static void Set(JsonObject o, string key, string? value)
    {
        if (value is not null)
        {
            o[key] = value;
        }
    }
}
