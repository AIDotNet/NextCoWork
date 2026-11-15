using System.Text;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Session;
using Ncw.Core.Text;

namespace Ncw.Agent.Skills;

/// <summary>
/// Skill 扫描(2.x <c>kernel/skill/load.ts</c>)——照搬 Claude Code 的目录约定:
/// <code>
/// &lt;数据根&gt;/skills/&lt;name&gt;/SKILL.md                    全局(设备本机)
/// &lt;项目&gt;/.next-cowork/skills/&lt;name&gt;/SKILL.md          项目(项目的执行环境;同名时项目胜出)
/// </code>
/// 是<b>目录</b>不是单文件:Skill 常常要带脚本、模板、参考资料(子代理定义不带资产,所以那边是单文件)。
/// <para>
/// ★ <b>永不抛</b>:目录不存在、权限不足、某个 SKILL.md 是坏的,全部变成诊断。一个坏文件让整次扫描失败的话,用户看到的是「所有 Skill 都不见了」。
/// 唯一的例外是执行环境连不上(<c>Disconnected</c> 等):那不是文件的问题,不能判成「没有 Skill」,原样抛给调用方。
/// 解析器(<see cref="FrontmatterParser"/>)只回答「这个 YAML 能不能读」,「这条 Skill 能不能用」(缺 description、名字不合法、正文为空)是这里的判断。
/// </para>
/// </summary>
public static class SkillLoader
{
    /// <summary>安装器自己写进包目录的元数据文件(包清单)。</summary>
    public const string PackageMetadataFile = ".nextcowork-package.json";

    /// <summary>内置 Skill 的哨兵文件(见 <see cref="BundledSkills"/>)。</summary>
    public const string BundledMarker = ".nextcowork-bundled";

    private static readonly HashSet<string> NonAssetFiles = new(StringComparer.Ordinal) { "SKILL.md", PackageMetadataFile, BundledMarker };

    private sealed record Loaded(Skill Skill, IExecutionEnvironment Environment);

    public static async Task<SkillScanResult> ScanAsync(SkillScanInput input, CancellationToken ct = default)
    {
        var diagnostics = new List<SkillDiagnostic>();
        var byName = new Dictionary<string, Loaded>(StringComparer.Ordinal);
        var order = new List<string>();

        // ★ 插件层最先进:后进的覆盖先进的,所以插件的优先级最低。反过来的话,用户在 .next-cowork/skills/ 里放一条同名的覆盖规则会毫无反应,而界面上两条都在。
        await ScanPluginRootsAsync(input, byName, order, diagnostics, ct).ConfigureAwait(false);

        // ★ 顺序即优先级:全局先进,项目后进覆盖同名。
        if (input.GlobalEnvironment is { } globalEnv && input.GlobalRoot is { } globalRoot)
        {
            await ScanRootAsync(globalEnv, globalRoot, SkillScope.Global, byName, order, diagnostics, ct).ConfigureAwait(false);
        }

        if (input.ProjectEnvironment is { } projectEnv && input.ProjectRoot is { } projectRoot)
        {
            await ScanRootAsync(projectEnv, projectRoot, SkillScope.Project, byName, order, diagnostics, ct).ConfigureAwait(false);
        }

        var skills = new List<Skill>(order.Count);
        foreach (var name in order)
        {
            var loaded = byName[name];
            var skill = loaded.Skill;
            // SSH 会话:非项目 Skill(全局、插件)的资产在设备本机,而这一轮的命令跑在远端。目录里只有 SKILL.md / 元数据 / 哨兵的是纯文本,照样能用;
            // 否则模型会照着正文去跑一个只存在于设备上的 scripts/check.py,表现为一条找不到文件的命令失败,而那个路径看上去完全合理。
            if (input.ProjectIsRemote && skill.Scope != SkillScope.Project)
            {
                try
                {
                    var contents = await loaded.Environment.Fs.ListAsync(EnvPath.Parse(skill.Directory, loaded.Environment.Facts.PathStyle), ct).ConfigureAwait(false);
                    if (contents.Any(e => !NonAssetFiles.Contains(e.Name)))
                    {
                        skill = skill with { ClientAssetsOnly = true };
                    }
                }
                catch (EnvironmentException e) when (!IsConnection(e))
                {
                    skill = skill with { ClientAssetsOnly = true };
                }
            }

            skills.Add(skill);
        }

        return new SkillScanResult(skills, diagnostics);
    }

    // ───────────────────────── 插件层 ─────────────────────────

    private static async Task ScanPluginRootsAsync(
        SkillScanInput input, Dictionary<string, Loaded> output, List<string> order, List<SkillDiagnostic> diagnostics, CancellationToken ct)
    {
        // 两个插件贡献同名 Skill 时先来的赢并且出诊断(按调用方给的顺序,所以结果是确定的)。不出诊断的话,后装的那个插件的 Skill 会凭空消失。
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in input.PluginRoots ?? [])
        {
            if (output.Count >= SkillLimits.MaxSkills)
            {
                diagnostics.Add(TooMany(root.Directory.Value));
                return;
            }

            var dirName = root.Directory.FileName;
            Skill? loaded;
            try
            {
                // 和另外两层一样过路径守卫:插件包里的 SKILL.md 可以是一条软链(目录安装没有 zip 安装那道符号链接闸),指向 ~/.ssh/id_rsa 的软链会被原样读进正文。
                var file = await root.Environment.Paths.ResolveWithinAsync(root.Directory, "SKILL.md", ct).ConfigureAwait(false);
                loaded = await LoadOneAsync(root.Environment, file, root.Directory, dirName, SkillScope.Plugin, diagnostics, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PathEscape)
            {
                diagnostics.Add(new SkillDiagnostic(
                    root.Directory.Join("SKILL.md").Value, SkillDiagnosticKeys.PluginSkillOutside,
                    "This plugin Skill points outside its package; skipped.", Params(("plugin", root.PluginId))));
                continue;
            }
            catch (EnvironmentException e) when (!IsConnection(e))
            {
                diagnostics.Add(Unreadable(root.Directory.Join("SKILL.md").Value, e));
                continue;
            }

            if (loaded is null)
            {
                continue;
            }

            if (owners.TryGetValue(loaded.Name, out var previous))
            {
                diagnostics.Add(new SkillDiagnostic(
                    root.Directory.Join("SKILL.md").Value, SkillDiagnosticKeys.PluginNameClash,
                    $"Plugins {root.PluginId} and {previous} both provide a Skill named \"{loaded.Name}\"; this one was skipped.",
                    Params(("name", loaded.Name), ("plugin", root.PluginId), ("other", previous))));
                continue;
            }

            owners[loaded.Name] = root.PluginId;
            loaded = loaded with { Source = loaded.Source with { Kind = SkillSourceKind.Plugin, PluginId = root.PluginId } };
            Put(output, order, new Loaded(loaded, root.Environment));
        }
    }

    // ───────────────────────── 全局 / 项目层 ─────────────────────────

    private static async Task ScanRootAsync(
        IExecutionEnvironment env, EnvPath root, SkillScope scope, Dictionary<string, Loaded> output, List<string> order, List<SkillDiagnostic> diagnostics, CancellationToken ct)
    {
        IReadOnlyList<FsDirEntry> entries;
        try
        {
            if (!await env.Fs.ExistsAsync(root, ct).ConfigureAwait(false))
            {
                return; // 没有 skills 目录是常态,不是错误
            }

            entries = await env.Fs.ListAsync(root, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (!IsConnection(e))
        {
            diagnostics.Add(new SkillDiagnostic(root.Value, SkillDiagnosticKeys.DirectoryUnreadable, $"Could not read this directory: {e.Message}"));
            return;
        }

        foreach (var entry in entries)
        {
            // 软链目录也算候选(围栏在下面判:指向 skills 内部的允许,指向外面的拒绝)
            if (!entry.Info.IsDirectory && !entry.Info.IsSymlink)
            {
                continue;
            }

            var display = root.Join(entry.Name).Value;
            if (output.Count >= SkillLimits.MaxSkills)
            {
                diagnostics.Add(TooMany(root.Value));
                return;
            }

            // ★ 目录名先过名字规则:不只是「规范命名」——名字会进系统提示词、被模型原样当入参,一个带换行的目录名两样都能搞坏。
            if (!SkillLimits.IsValidName(entry.Name))
            {
                diagnostics.Add(new SkillDiagnostic(display, SkillDiagnosticKeys.DirectoryNameInvalid, "The directory name is not a valid Skill name; skipped.", Params(("directory", entry.Name))));
                continue;
            }

            EnvPath dir;
            try
            {
                // ★ 这一步挡软链逃逸:skills/evil -> / 之后,「扫描 skills 目录」就变成了「扫描整个磁盘」(而 .next-cowork/ 是跟着仓库 clone 下来的)。
                dir = await env.Paths.ResolveWithinAsync(root, entry.Name, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PathEscape)
            {
                diagnostics.Add(new SkillDiagnostic(display, SkillDiagnosticKeys.OutsideRoot, "This directory resolves to somewhere outside the skills directory; skipped."));
                continue;
            }
            catch (EnvironmentException e) when (!IsConnection(e))
            {
                diagnostics.Add(Unreadable(display, e));
                continue;
            }

            try
            {
                var file = await env.Paths.ResolveWithinAsync(dir, "SKILL.md", ct).ConfigureAwait(false);
                if (await LoadOneAsync(env, file, dir, entry.Name, scope, diagnostics, ct).ConfigureAwait(false) is { } loaded)
                {
                    Put(output, order, new Loaded(loaded, env));
                }
            }
            catch (EnvironmentException e) when (!IsConnection(e))
            {
                diagnostics.Add(Unreadable(dir.Join("SKILL.md").Value, e));
            }
        }
    }

    private static void Put(Dictionary<string, Loaded> output, List<string> order, Loaded loaded)
    {
        // 同名:后进的覆盖先进的,位置不变(最终按名字排序,所以位置无关紧要)
        if (!output.ContainsKey(loaded.Skill.Name))
        {
            order.Add(loaded.Skill.Name);
        }

        output[loaded.Skill.Name] = loaded;
    }

    private static async Task<Skill?> LoadOneAsync(
        IExecutionEnvironment env, EnvPath file, EnvPath directory, string directoryName, SkillScope scope, List<SkillDiagnostic> diagnostics, CancellationToken ct)
    {
        string raw;
        try
        {
            if (!await env.Fs.ExistsAsync(file, ct).ConfigureAwait(false))
            {
                diagnostics.Add(new SkillDiagnostic(file.Value, SkillDiagnosticKeys.SkillMdMissing, "This directory has no SKILL.md; skipped.", Params(("directory", directoryName))));
                return null;
            }

            // ★ 文件字节数的闸在解码之前:一个 500KB 的 SKILL.md 只读前 256KB 就够判断它有没有 frontmatter。
            raw = Encoding.UTF8.GetString(await env.Fs.ReadHeadAsync(file, SkillLimits.FileMaxBytes, ct).ConfigureAwait(false));
        }
        catch (EnvironmentException e) when (!IsConnection(e))
        {
            diagnostics.Add(Unreadable(file.Value, e));
            return null;
        }

        var fm = FrontmatterParser.Parse(raw);
        foreach (var skipped in fm.Skipped)
        {
            diagnostics.Add(new SkillDiagnostic(file.Value, SkillDiagnosticKeys.Frontmatter, skipped));
        }

        // ★ frontmatter 里的 name 胜过目录名,但不一致时要说一声(用户把目录改名之后会发现 /old-name 还在)。
        var declared = FrontmatterParser.String(fm, "name");
        var name = declared ?? directoryName;
        if (declared is not null && declared != directoryName)
        {
            diagnostics.Add(new SkillDiagnostic(
                file.Value, SkillDiagnosticKeys.NameMismatch,
                $"The name \"{declared}\" in the frontmatter does not match the directory name \"{directoryName}\"; the frontmatter name wins.",
                Params(("declared", declared), ("directory", directoryName))));
        }

        if (!SkillLimits.IsValidName(name))
        {
            diagnostics.Add(new SkillDiagnostic(file.Value, SkillDiagnosticKeys.NameInvalid, $"The name \"{name}\" is not valid; this Skill was dropped.", Params(("name", name))));
            return null;
        }

        // ★ 缺 description 整条作废,而不是拿名字凑一个:渐进披露里描述是模型唯一的判断依据——没有它,模型要么永远不调,要么见什么都调。
        var description = FrontmatterParser.String(fm, "description");
        if (description is null)
        {
            diagnostics.Add(new SkillDiagnostic(
                file.Value, SkillDiagnosticKeys.DescriptionMissing,
                "The frontmatter has no description, so the model cannot tell when to use this Skill; it was dropped."));
            return null;
        }

        var body = PromptText.Clamp(PromptText.StripControlChars(fm.Body).Trim(), SkillLimits.BodyMax);
        if (body.Length == 0)
        {
            diagnostics.Add(new SkillDiagnostic(file.Value, SkillDiagnosticKeys.BodyEmpty, "The body is empty; this Skill was dropped."));
            return null;
        }

        var meta = await ReadPackageMetadataAsync(env, directory, ct).ConfigureAwait(false);
        var model = FrontmatterParser.String(fm, "model");
        return new Skill(
            name,
            PromptText.Clamp(PromptText.StripControlChars(description), SkillLimits.DescriptionMax),
            FrontmatterParser.String(fm, "category") is { } category ? PromptText.StripControlChars(category) : Uncategorized,
            new SkillSource(meta?.Kind ?? SkillSourceKind.Folder, file.Value, meta?.Version, meta?.Sha256),
            scope,
            body,
            model is null ? null : PromptText.StripControlChars(model),
            // allowed-tools 是 CC 的写法;allowedTools 是有人会顺手写的变体。没写 = null(空数组会被下游读成「一个工具都不许用」)
            FrontmatterParser.List(fm, "allowed-tools") ?? FrontmatterParser.List(fm, "allowedTools"))
        {
            Directory = directory.Value,
        };
    }

    /// <summary>没写 category 时的分类名(领域数据,不是界面文字;界面按这个值显示本地化名字)。</summary>
    public const string Uncategorized = "未分类";

    private sealed record PackageMeta(SkillSourceKind Kind, string? Version, string? Sha256);

    /// <summary>安装器写的包清单。只认 <c>sourceKind: zip</c> 且带 sha256 的;读不了 / 没有(目录安装、旧包)就当没有。</summary>
    private static async Task<PackageMeta?> ReadPackageMetadataAsync(IExecutionEnvironment env, EnvPath directory, CancellationToken ct)
    {
        try
        {
            var path = await env.Paths.ResolveWithinAsync(directory, PackageMetadataFile, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await env.Fs.ReadHeadAsync(path, 4096, ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sourceKind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "zip"
                && root.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String)
            {
                var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                return new PackageMeta(SkillSourceKind.Zip, string.IsNullOrEmpty(version) ? null : version, sha.GetString());
            }
        }
        catch (EnvironmentException e) when (IsConnection(e))
        {
            throw;
        }
        catch (Exception e) when (e is EnvironmentException or JsonException)
        {
            // 目录安装和旧包没有元数据文件
        }

        return null;
    }

    internal static bool IsConnection(EnvironmentException e) =>
        e.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration;

    private static SkillDiagnostic Unreadable(string path, EnvironmentException e) =>
        new(path, SkillDiagnosticKeys.FileUnreadable, $"Could not read this file: {e.Message}");

    private static SkillDiagnostic TooMany(string path) =>
        new(path, SkillDiagnosticKeys.TooMany, $"More than {SkillLimits.MaxSkills} Skills; the rest were not loaded.", Params(("max", SkillLimits.MaxSkills.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    internal static IReadOnlyDictionary<string, string> Params(params (string Key, string Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);
}
