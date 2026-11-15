using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Skills;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 清单解析与校验(2.x <c>shared/plugin/manifest.ts</c> 的 <c>parsePluginManifest</c>)。取向:<b>装载期严格,运行期宽容</b>——读不懂的清单整份拒绝(半份生效的插件会在某个贡献点上凭空消失),
/// 但拒绝的方式是返回错误、永不抛。不用反射序列化:直接走 <see cref="JsonElement"/>。
/// <para>3.0 的收窄:<c>main</c> 必须是单文件 ESM 的 <c>.js</c> / <c>.mjs</c>(2.x 的清单本来就要求「单文件 ESM .js 打包」,TypeScript / JSX 由插件作者自己编译);<c>nativeComponents</c> 不支持。</para>
/// </summary>
public static partial class PluginManifestParser
{
    public const int MaxContributionsPerKind = 100;

    /// <summary>清单文件本身的上限(只为防一份被塞了几 MB 的清单把装载拖住)。</summary>
    public const int MaxManifestBytes = 256 * 1024;

    /// <summary>包里放 skill 的那层目录(清单校验、打包、服务端上架三方约定)。</summary>
    public const string SkillContributionDirectory = "skills";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\.[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^%[A-Za-z0-9_][A-Za-z0-9_.-]*%$", RegexOptions.CultureInvariant)]
    private static partial Regex L10nRefPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlashPattern();

    [GeneratedRegex(@"^[a-z0-9.-]+(:\d{1,5})?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[a-z0-9._+-]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommandStemPattern();

    [GeneratedRegex(@"\.(exe|cmd|bat|ps1)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsExecutable();

    [GeneratedRegex(@"^[a-zA-Z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetter();

    private static readonly string[] ActivationPrefixes = ["onCommand:", "onView:", "onCustomEditor:", "onTool:", "onWorkspaceContains:", "onWebApp:", "onSlashCommand:"];

    private static readonly string[] ToolShapes = ["reasoning", "read", "mutate", "search", "command", "network", "orchestration", "external"];

    /// <summary>认得字段名、但 3.0 不实现的贡献点。</summary>
    private static readonly string[] UnsupportedKeys = ["customEditors", "views", "webApps", "cardViews", "themes", "agents", "modes", "documentEngines"];

    private static readonly string[] SupportedKeys = ["commands", "menus", "tools", "skills", "configuration", "slashCommands", "keybindings"];

    /// <summary><c>kind: webapp</c> 允许出现的贡献点:其余一律报错而不是忽略(一个没有代码的插件声明 tools / commands = 注册了一个永远没有处理函数的东西)。</summary>
    private static readonly string[] WebAppAllowed = ["webApps", "themes", "skills", "agents", "modes"];

    public static ManifestParseResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return new ManifestParseResult(null, [new ManifestError("", "package.json is not valid JSON")], []);
        }
    }

    public static ManifestParseResult Parse(JsonElement root)
    {
        var errors = new List<ManifestError>();
        var warnings = new List<ManifestError>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ManifestParseResult(null, [new ManifestError("", "manifest must be a JSON object")], []);
        }

        var name = Str(root, "name");
        if (!NamePattern().IsMatch(name))
        {
            errors.Add(new ManifestError("name", "must match ^[a-z0-9][a-z0-9-]{0,63}$"));
        }

        var publisher = Str(root, "publisher");
        if (!NamePattern().IsMatch(publisher))
        {
            errors.Add(new ManifestError("publisher", "must match ^[a-z0-9][a-z0-9-]{0,63}$"));
        }

        var version = Str(root, "version");
        if (!PluginVersions.IsVersion(version))
        {
            errors.Add(new ManifestError("version", "must be a semver string"));
        }

        var displayName = Str(root, "displayName") is { Length: > 0 } dn ? dn : name;
        var description = Str(root, "description");

        // ★ kind 必须先解析:main 的校验规则和 contributes 里哪些贡献点合法都由它分流
        var kindText = Str(root, "kind");
        var kind = kindText == "webapp" ? PluginKind.WebApp : PluginKind.Extension;
        if (kindText is not ("" or "extension" or "webapp"))
        {
            errors.Add(new ManifestError("kind", $"unknown plugin kind \"{kindText}\"; expected \"extension\" or \"webapp\""));
        }

        var engines = root.TryGetProperty("engines", out var enginesElement) ? Str(enginesElement, "nextcowork") : "";
        if (engines.Length == 0)
        {
            errors.Add(new ManifestError("engines.nextcowork", "is required"));
        }
        else if (PluginVersions.ParseRange(engines) is null)
        {
            errors.Add(new ManifestError("engines.nextcowork", $"unsupported range: {engines}"));
        }

        var main = Str(root, "main");
        if (kind == PluginKind.WebApp)
        {
            // ★ 写了 main 的 webapp 清单整份拒绝,不是忽略:忽略的话作者会以为自己的代码在跑
            if (main.Length > 0)
            {
                errors.Add(new ManifestError("main", "a \"webapp\" plugin runs no code; remove \"main\""));
            }
        }
        else if (main.Length == 0)
        {
            errors.Add(new ManifestError("main", "is required"));
        }
        else if (!IsSafeRelativePath(main))
        {
            errors.Add(new ManifestError("main", "must be a relative path inside the package"));
        }
        else if (!main.EndsWith(".js", StringComparison.Ordinal) && !main.EndsWith(".mjs", StringComparison.Ordinal))
        {
            errors.Add(new ManifestError("main", "must be a single-file ES module (.js or .mjs); compile TypeScript before packaging"));
        }

        var l10n = Str(root, "l10n");
        if (l10n.Length > 0 && !IsSafeRelativePath(l10n))
        {
            errors.Add(new ManifestError("l10n", "must be a relative path inside the package"));
        }

        var icon = Str(root, "icon");
        if (icon.Length > 0 && !IsSafeRelativePath(icon))
        {
            errors.Add(new ManifestError("icon", "must be a relative path inside the package"));
        }

        var activationEvents = new List<string>();
        foreach (var evt in StrList(root, "activationEvents"))
        {
            if (evt == "onStartup" || ActivationPrefixes.Any(p => evt.StartsWith(p, StringComparison.Ordinal) && evt.Length > p.Length))
            {
                activationEvents.Add(evt);
            }
            else
            {
                errors.Add(new ManifestError("activationEvents", $"unknown activation event: {evt}"));
            }
        }

        var permissions = PermissionList(root, "permissions", errors);
        var optional = PermissionList(root, "optionalPermissions", errors);

        var hostPermissions = new List<string>();
        foreach (var pattern in StrList(root, "hostPermissions"))
        {
            if (IsHostPattern(pattern))
            {
                hostPermissions.Add(pattern);
            }
            else
            {
                errors.Add(new ManifestError("hostPermissions", $"must look like https://host/path* : {pattern}"));
            }
        }

        // 没声明 net 却写了 hostPermissions 是警告不是错误:它通常是作者把 net 挪进 optionalPermissions 时留下的
        if (hostPermissions.Count > 0 && !permissions.Contains(PluginPermissions.Net) && !optional.Contains(PluginPermissions.Net))
        {
            warnings.Add(new ManifestError("hostPermissions", "has no effect without the \"net\" permission"));
        }

        var allowedCommands = new List<string>();
        foreach (var command in StrList(root, "allowedCommands"))
        {
            if (IsCommandStem(command))
            {
                allowedCommands.Add(command);
            }
            else
            {
                errors.Add(new ManifestError("allowedCommands", $"must be a bare executable name without a path: {command}"));
            }
        }

        if (allowedCommands.Count > 0 && !permissions.Contains(PluginPermissions.Process) && !optional.Contains(PluginPermissions.Process))
        {
            warnings.Add(new ManifestError("allowedCommands", "has no effect without the \"process\" permission"));
        }

        var contributes = ParseContributes(root.TryGetProperty("contributes", out var c) ? c : default, errors, kind);

        var id = $"{publisher}.{name}";
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            foreach (var dep in deps.EnumerateObject())
            {
                var range = dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString()!.Trim() : "";
                if (!IdPattern().IsMatch(dep.Name))
                {
                    errors.Add(new ManifestError($"dependencies.{dep.Name}", "not a valid plugin id (publisher.name)"));
                }
                else if (dep.Name == id)
                {
                    errors.Add(new ManifestError($"dependencies.{dep.Name}", "a plugin cannot depend on itself"));
                }
                else if (PluginVersions.ParseRange(range) is null)
                {
                    errors.Add(new ManifestError($"dependencies.{dep.Name}", $"unsupported range: {range}"));
                }
                else
                {
                    dependencies[dep.Name] = range;
                }
            }
        }

        if (root.TryGetProperty("nativeComponents", out _))
        {
            errors.Add(new ManifestError("nativeComponents", "native components are not supported by this host"));
        }

        if (errors.Count > 0)
        {
            return new ManifestParseResult(null, errors, warnings);
        }

        var manifest = new PluginManifest(
            id, name, publisher, kind, displayName, description, version,
            Str(root, "license") is { Length: > 0 } license ? license : null,
            icon.Length == 0 ? null : icon,
            [.. StrList(root, "categories").Take(8)], [.. StrList(root, "keywords").Take(16)],
            engines, main, l10n.Length == 0 ? null : l10n, activationEvents,
            permissions, optional, hostPermissions, allowedCommands, dependencies, contributes);
        return new ManifestParseResult(manifest, [], warnings);
    }

    // ───────────────────────── 贡献点 ─────────────────────────

    private static PluginContributes ParseContributes(JsonElement raw, List<ManifestError> errors, PluginKind kind)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return PluginContributes.Empty;
        }

        var unsupported = new List<string>();
        foreach (var property in raw.EnumerateObject())
        {
            if (!SupportedKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                unsupported.Add(property.Name);
                if (!UnsupportedKeys.Contains(property.Name, StringComparer.Ordinal))
                {
                    continue;
                }
            }

            if (kind == PluginKind.WebApp && !WebAppAllowed.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add(new ManifestError($"contributes.{property.Name}", $"a \"webapp\" plugin runs no code, so it cannot contribute {property.Name}"));
            }
        }

        var commands = new List<PluginCommandContribution>();
        foreach (var item in ObjList(raw, "commands"))
        {
            var command = Str(item, "command");
            var title = Str(item, "title");
            if (command.Length == 0)
            {
                errors.Add(new ManifestError("contributes.commands", "command id is required"));
                continue;
            }

            if (!L10nRefPattern().IsMatch(title))
            {
                errors.Add(new ManifestError($"contributes.commands.{command}.title", "must be a %l10nKey% reference, not literal copy"));
                continue;
            }

            var iconFile = Str(item, "iconFile");
            if (iconFile.Length > 0)
            {
                if (!IsSafeRelativePath(iconFile))
                {
                    errors.Add(new ManifestError($"contributes.commands.{command}.iconFile", "must be a relative path inside the package"));
                    continue;
                }

                if (!iconFile.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !iconFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new ManifestError($"contributes.commands.{command}.iconFile", "must be an .svg or .png file"));
                    continue;
                }
            }

            commands.Add(new PluginCommandContribution(command, title, Str(item, "icon") is { Length: > 0 } ic ? ic : null, iconFile.Length == 0 ? null : iconFile));
        }

        var menus = new Dictionary<string, IReadOnlyList<PluginMenuContribution>>(StringComparer.Ordinal);
        if (raw.TryGetProperty("menus", out var menusElement) && menusElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var menu in menusElement.EnumerateObject())
            {
                var items = new List<PluginMenuContribution>();
                if (menu.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in menu.Value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Take(MaxContributionsPerKind))
                    {
                        if (Str(item, "command") is { Length: > 0 } command)
                        {
                            items.Add(new PluginMenuContribution(command, Str(item, "group") is { Length: > 0 } g ? g : null, Str(item, "when") is { Length: > 0 } w ? w : null));
                        }
                    }
                }

                if (items.Count > 0)
                {
                    menus[menu.Name] = items;
                }
            }
        }

        var tools = new List<PluginToolContribution>();
        foreach (var item in ObjList(raw, "tools"))
        {
            var toolName = Str(item, "name");
            var title = Str(item, "title");
            if (toolName.Length == 0)
            {
                errors.Add(new ManifestError("contributes.tools", "tool name is required"));
                continue;
            }

            if (!PluginToolNames.IsValid(toolName))
            {
                errors.Add(new ManifestError($"contributes.tools.{toolName}.name", "must match ^[A-Za-z0-9_-]{1,48}$"));
                continue;
            }

            if (!L10nRefPattern().IsMatch(title))
            {
                errors.Add(new ManifestError($"contributes.tools.{toolName}.title", "must be a %l10nKey% reference"));
                continue;
            }

            var shape = Str(item, "shape");
            if (shape.Length > 0 && !ToolShapes.Contains(shape, StringComparer.Ordinal))
            {
                errors.Add(new ManifestError($"contributes.tools.{toolName}.shape", $"unknown shape: {shape}"));
                continue;
            }

            string? cardTitle = null;
            string? cardSummary = null;
            if (item.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
            {
                var ct = Str(card, "title");
                var cs = Str(card, "summary");
                if ((ct.Length > 0 && !L10nRefPattern().IsMatch(ct)) || (cs.Length > 0 && !L10nRefPattern().IsMatch(cs)))
                {
                    errors.Add(new ManifestError($"contributes.tools.{toolName}.card", "title and summary must be %l10nKey% references"));
                    continue;
                }

                cardTitle = ct.Length == 0 ? null : ct;
                cardSummary = cs.Length == 0 ? null : cs;
            }

            if (tools.Any(t => t.Name == toolName))
            {
                errors.Add(new ManifestError($"contributes.tools.{toolName}", "duplicate tool name"));
                continue;
            }

            tools.Add(new PluginToolContribution(toolName, title, Str(item, "icon") is { Length: > 0 } ic ? ic : null, shape.Length == 0 ? null : shape, cardTitle, cardSummary));
        }

        var keybindings = new List<PluginKeybindingContribution>();
        foreach (var item in ObjList(raw, "keybindings"))
        {
            var command = Str(item, "command");
            var key = Str(item, "key");
            if (command.Length > 0 && key.Length > 0)
            {
                keybindings.Add(new PluginKeybindingContribution(command, key, Str(item, "when") is { Length: > 0 } w ? w : null));
            }
        }

        // 斜杠命令:command 必须是本清单里已经声明过的命令——指向一条不存在的命令时,用户在输入框里能打出它、选中它、然后什么都不发生
        var slash = new List<PluginSlashCommandContribution>();
        foreach (var item in ObjList(raw, "slashCommands"))
        {
            var slashName = Str(item, "name");
            var command = Str(item, "command");
            var title = Str(item, "title");
            var description = Str(item, "description");
            if (!SlashPattern().IsMatch(slashName))
            {
                errors.Add(new ManifestError("contributes.slashCommands", $"slash command name must match ^[a-z0-9][a-z0-9-]{{0,31}}$: {slashName}"));
                continue;
            }

            if (!commands.Any(c => c.Command == command))
            {
                errors.Add(new ManifestError($"contributes.slashCommands.{slashName}.command", $"must reference a command declared in contributes.commands: {command}"));
                continue;
            }

            if (!L10nRefPattern().IsMatch(title) || (description.Length > 0 && !L10nRefPattern().IsMatch(description)))
            {
                errors.Add(new ManifestError($"contributes.slashCommands.{slashName}.title", "title and description must be %l10nKey% references"));
                continue;
            }

            slash.Add(new PluginSlashCommandContribution(slashName, command, title, description.Length == 0 ? null : description));
        }

        // ★ skill 目录必须落在包内的 skills/<名字> 下:打包器只拷固定的那几个目录;名字来自目录名,必须是确定的东西
        var skills = new List<string>();
        foreach (var item in ObjList(raw, "skills"))
        {
            var path = Str(item, "path");
            if (!IsSafeRelativePath(path))
            {
                errors.Add(new ManifestError("contributes.skills", $"not a package-relative path: {path}"));
                continue;
            }

            var segments = path.TrimEnd('/').Split('/');
            if (segments.Length != 2 || segments[0] != SkillContributionDirectory)
            {
                errors.Add(new ManifestError("contributes.skills", $"must be \"{SkillContributionDirectory}/<name>\": the packager only ships {SkillContributionDirectory}/ (got {path})"));
                continue;
            }

            if (!SkillLimits.IsValidName(segments[1]))
            {
                errors.Add(new ManifestError("contributes.skills", $"skill directory name is not a valid Skill name (got {path})"));
                continue;
            }

            skills.Add(string.Join('/', segments));
        }

        PluginConfigurationContribution? configuration = null;
        if (raw.TryGetProperty("configuration", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
        {
            configuration = ParseConfiguration(cfg, errors);
        }

        return new PluginContributes(commands, menus, tools, skills, configuration, slash, keybindings, unsupported);
    }

    private static PluginConfigurationContribution? ParseConfiguration(JsonElement cfg, List<ManifestError> errors)
    {
        var title = Str(cfg, "title");
        if (!L10nRefPattern().IsMatch(title))
        {
            errors.Add(new ManifestError("contributes.configuration.title", "must be a %l10nKey% reference"));
            return null;
        }

        var properties = new Dictionary<string, PluginConfigurationProperty>(StringComparer.Ordinal);
        if (cfg.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = Str(prop.Value, "type");
                if (type is not ("boolean" or "string" or "number" or "enum"))
                {
                    continue;
                }

                var propTitle = Str(prop.Value, "title");
                if (!L10nRefPattern().IsMatch(propTitle))
                {
                    errors.Add(new ManifestError($"contributes.configuration.properties.{prop.Name}.title", "must be a %l10nKey% reference"));
                    continue;
                }

                string? defaultString = null;
                double? defaultNumber = null;
                bool? defaultBool = null;
                if (prop.Value.TryGetProperty("default", out var def))
                {
                    switch (def.ValueKind)
                    {
                        case JsonValueKind.String:
                            defaultString = def.GetString();
                            break;
                        case JsonValueKind.Number:
                            defaultNumber = def.GetDouble();
                            break;
                        case JsonValueKind.True or JsonValueKind.False:
                            defaultBool = def.GetBoolean();
                            break;
                    }
                }

                properties[prop.Name] = new PluginConfigurationProperty(type, propTitle, Str(prop.Value, "description") is { Length: > 0 } d ? d : null, defaultString, defaultNumber, defaultBool, StrList(prop.Value, "enum"));
            }
        }

        return new PluginConfigurationContribution(title, properties);
    }

    // ───────────────────────── 小工具 ─────────────────────────

    /// <summary>
    /// 包内相对路径。挡的不只是 <c>..</c>:绝对路径、盘符、反斜杠、NUL 全在这里挡掉。这是<b>第一道</b>防线,运行期还会再做一次 realpath 归一(软链是这一层看不出来的)。
    /// </summary>
    public static bool IsSafeRelativePath(string value)
    {
        if (value.Length == 0 || value.Length > 512 || value.StartsWith('/') || value.StartsWith('\\') || DriveLetter().IsMatch(value) || value.Contains('\\') || value.Contains('\0'))
        {
            return false;
        }

        return !value.Split('/').Any(s => s == "..");
    }

    /// <summary><c>https://host/path*</c>。不接受 http,不接受 <c>*</c> 当主机名。</summary>
    public static bool IsHostPattern(string value)
    {
        if (!value.StartsWith("https://", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = value["https://".Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var host = slash < 0 ? rest : rest[..slash];
        return host.Length > 0 && !host.Contains('*') && HostPattern().IsMatch(host);
    }

    /// <summary><c>allowedCommands</c> 的一条合法取值:裸可执行名(拒绝路径分隔符和 .exe/.cmd/.bat/.ps1 后缀,而不是默默剥掉:清单上写着的和实际生效的必须是同一个东西)。</summary>
    public static bool IsCommandStem(string value) =>
        value.Length is > 0 and <= 64 && !value.Contains('/') && !value.Contains('\\') && !WindowsExecutable().IsMatch(value) && CommandStemPattern().IsMatch(value);

    /// <summary><c>publisher.name</c>。</summary>
    public static bool IsPluginId(string value) => IdPattern().IsMatch(value);

    public static bool IsL10nRef(string value) => L10nRefPattern().IsMatch(value);

    private static string Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static List<string> StrList(JsonElement element, string property)
    {
        var list = new List<string>();
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString()!.Trim() is { Length: > 0 } s && !list.Contains(s))
            {
                list.Add(s);
                if (list.Count >= MaxContributionsPerKind)
                {
                    break;
                }
            }
        }

        return list;
    }

    private static IEnumerable<JsonElement> ObjList(JsonElement element, string property) =>
        element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Take(MaxContributionsPerKind)
            : [];

    private static List<string> PermissionList(JsonElement root, string property, List<ManifestError> errors)
    {
        var list = new List<string>();
        foreach (var entry in StrList(root, property))
        {
            if (PluginPermissions.IsValid(entry))
            {
                list.Add(entry);
            }
            else
            {
                errors.Add(new ManifestError(property, $"unknown permission \"{entry}\"; expected one of {string.Join(", ", PluginPermissions.All)}"));
            }
        }

        return list;
    }
}

/// <summary>插件工具名的形状(与外部名规则兼容;注册表还会再截断去重一次)。</summary>
public static partial class PluginToolNames
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,48}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string name) => Pattern().IsMatch(name);

    /// <summary>
    /// 内部 id:<c>plugin__&lt;publisher&gt;_&lt;name&gt;__&lt;tool&gt;</c>(和 MCP 的 <c>mcp__server__tool</c> 同一个形状;两个插件各注册一个 <c>search</c> 不会互相顶掉)。
    /// 用 <c>__</c> 分隔而不是 <c>.</c>:<c>.</c> 在外部名规则里不合法。
    /// </summary>
    public static string InternalId(string pluginId, string toolName) => $"plugin__{pluginId.Replace('.', '_').Replace('-', '_')}__{toolName}";

    /// <summary>工具来源名(<see cref="Ncw.Agent.Kernel.Session.ToolRegistry"/> 按来源批量注销)。</summary>
    public static string Source(string pluginId) => $"plugin:{pluginId}";
}
