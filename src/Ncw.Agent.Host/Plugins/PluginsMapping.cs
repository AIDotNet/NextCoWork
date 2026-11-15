using Ncw.Agent.Plugins;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host.Plugins;

/// <summary>插件域类型 → 协议 DTO(只有清单里的东西、状态、稳定 key 和结构化参数;没有任何凭据,活动日志里没有参数原文)。</summary>
internal static class PluginsMapping
{
    public static PluginsTopicState ToDto(PluginsSnapshot snapshot, string apiVersion) =>
        new(snapshot.Rev, apiVersion, [.. snapshot.Plugins.Select(ToDto)], [.. snapshot.Interactions.Select(ToDto)]);

    public static PluginInfoDto ToDto(InstalledPlugin p)
    {
        var m = p.Manifest;
        var c = m.Contributes;
        return new PluginInfoDto(
            m.Id, m.Name, m.Publisher, m.Kind == PluginKind.WebApp ? "webapp" : "extension", m.DisplayName, m.Description, m.Version, m.Engines, [.. m.Categories],
            PluginStatusNames.Of(p.Status), p.Enabled, p.Path,
            [.. p.Permissions.Required], [.. p.Permissions.Optional], [.. p.Permissions.Granted], [.. p.PendingPermissions],
            [.. m.HostPermissions], [.. m.AllowedCommands],
            [.. c.Commands.Select(x => new PluginCommandDto(x.Command, x.Title, x.Icon))],
            [.. c.Menus.SelectMany(menu => menu.Value.Select(x => new PluginMenuItemDto(menu.Key, x.Command, x.Group, x.When)))],
            [.. c.Tools.Select(x => new PluginToolDto(x.Name, x.Title, x.Shape, x.CardTitle, x.CardSummary, PluginToolNames.InternalId(m.Id, x.Name)))],
            [.. c.Skills],
            [.. c.SlashCommands.Select(x => new PluginSlashCommandDto(x.Name, x.Command, x.Title, x.Description))],
            [.. c.Keybindings.Select(x => new PluginKeybindingDto(x.Command, x.Key, x.When))],
            c.Configuration is null ? null : new PluginConfigurationDto(c.Configuration.Title, [.. c.Configuration.Properties.Select(kv => new PluginConfigPropertyDto(
                kv.Key, kv.Value.Type, kv.Value.Title, kv.Value.Description, [.. kv.Value.Enum], kv.Value.DefaultString, kv.Value.DefaultNumber, kv.Value.DefaultBool))]),
            [.. c.Unsupported],
            [.. p.Diagnostics.Select(d => new PluginDiagnosticDto(d.Path, d.Key, d.Message, d.Level, d.Params is null ? null : new Dictionary<string, string>(d.Params)))],
            [.. p.RegisteredTools], [.. p.RegisteredCommands],
            [.. p.Messages.Select(kv => new PluginMessagesDto(kv.Key, new Dictionary<string, string>(kv.Value)))],
            p.InstalledAt, p.UpdatedAt, p.Slug);
    }

    public static PluginInteractionDto ToDto(PluginInteraction i) => new(
        i.RequestId, i.PluginId, i.Kind, i.TitleKey, i.DetailKey, i.PlaceholderKey, [.. i.Items.Select(x => new PluginPickItemDto(x.Id, x.LabelKey))], i.Password, i.Danger, i.Initial,
        [.. i.Permissions], i.Command, i.Environment is null ? null : new PluginEnvironmentDto(i.Environment.DeviceId, i.Environment.Environment, i.Environment.Name, i.Environment.Type), i.CreatedAt);

    public static PluginActivityDto ToDto(PluginActivity a) => new(a.Ts, a.PluginId, a.Method, a.Summary, a.Verdict, a.DurationMs);

    public static PluginMarketItemDto ToDto(PluginMarketItem i) => new(
        i.PluginId, i.Slug, i.Publisher, i.DisplayName, i.Description, i.Category, i.IconUrl, i.DownloadCount, i.Version, i.Engines, [.. i.Permissions], [.. i.Skills], i.Author);

    public static PluginMarketDetailDto ToDto(PluginMarketDetail d) =>
        new(ToDto(d.Item), [.. d.Versions.Select(v => new PluginMarketVersionDto(v.Version, v.Changelog, v.PermissionEscalated, v.FileSize))]);

    public static PluginUpdateDto ToDto(PluginUpdate u) =>
        new(u.PluginId, u.Slug, u.DisplayName, u.CurrentVersion, u.LatestVersion, [.. u.EscalatedPermissions], u.FromMarket);

    public static PluginConfigValueDto ToDto(PluginConfigValue v) => new(v.Text, v.Number, v.Flag);

    public static PluginConfigValue? FromDto(PluginConfigValueDto? v) => v is null ? null : new PluginConfigValue(v.Text, v.Number, v.Flag);

    public static PluginsConfigurationResult ToConfigurationDto(string pluginId, IReadOnlyDictionary<string, PluginConfigValue> values) =>
        new(pluginId, values.ToDictionary(kv => kv.Key, kv => ToDto(kv.Value), StringComparer.Ordinal));
}
