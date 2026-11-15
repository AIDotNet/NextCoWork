using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 插件自己带的词条(<see cref="PluginInfoDto.Messages"/>:<c>plugin.&lt;id&gt;.&lt;key&gt;</c> → 文案)的解析。清单和提问里的标题 / 说明是 <c>%key%</c> 或 key 的形式,
/// 按「当前语言 → 同语种 → en-US → 第一份词条」取;都没有就显示去掉 <c>%</c> 的 key 本身(永远不显示成空)。这是插件作者给的数据,不是应用的界面文字。
/// </summary>
public static class PluginMessages
{
    public static string Resolve(PluginInfoDto plugin, string? key, string locale)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "";
        }

        var bare = key.Length > 2 && key[0] == '%' && key[^1] == '%' ? key[1..^1] : key;
        var full = $"plugin.{plugin.Id}.{bare}";
        foreach (var table in Candidates(plugin.Messages, locale))
        {
            if (table.Entries.TryGetValue(full, out var text) || table.Entries.TryGetValue(bare, out text))
            {
                return text;
            }
        }

        return bare;
    }

    private static IEnumerable<PluginMessagesDto> Candidates(PluginMessagesDto[] tables, string locale)
    {
        var language = locale.Split('-')[0];
        foreach (var table in tables.Where(t => string.Equals(t.Locale, locale, StringComparison.OrdinalIgnoreCase)))
        {
            yield return table;
        }

        foreach (var table in tables.Where(t => !string.Equals(t.Locale, locale, StringComparison.OrdinalIgnoreCase) && t.Locale.StartsWith(language, StringComparison.OrdinalIgnoreCase)))
        {
            yield return table;
        }

        foreach (var table in tables.Where(t => t.Locale.Equals("en-US", StringComparison.OrdinalIgnoreCase) && !t.Locale.StartsWith(language, StringComparison.OrdinalIgnoreCase)))
        {
            yield return table;
        }

        foreach (var table in tables)
        {
            yield return table;
        }
    }
}

/// <summary>插件能力的分类(纯函数,界面据此高亮)。能力全表在 Agent 的 <c>PluginPermissions</c>;控制端只认「哪些是危险的」,其余一律按名字显示。</summary>
public static class PluginPermissionInfo
{
    /// <summary>危险能力:能改文件、起进程、改仓库、读密钥、拦截 Agent 的工具调用。授权界面高亮,并在撤销 / 批准时多说一句。</summary>
    public static bool IsDangerous(string permission) => permission is "process" or "workspace.write" or "scm.write" or "secrets" or "agent.intercept";
}

/// <summary>插件状态的分类。</summary>
public static class PluginStatusInfo
{
    /// <summary>状态点的色调:<c>ok</c> / <c>warn</c> / <c>bad</c> / <c>mute</c>(给 <c>NcwStatusDot</c>)。</summary>
    public static string Tone(string status) => status switch
    {
        PluginStatuses.Active => "ok",
        PluginStatuses.Activating or PluginStatuses.PendingApproval => "warn",
        PluginStatuses.Error or PluginStatuses.Faulted => "bad",
        _ => "mute",
    };
}
