using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Plugins;

public static class PluginLimits
{
    public const int MaxZipBytes = 20 * 1024 * 1024;
    public const long MaxExpandedBytes = 50L * 1024 * 1024;
    public const int MaxEntries = 2000;
    public const int MaxDepth = 12;

    /// <summary><c>plugins.installPackage</c> 请求里 zip 字节的上限:协议单帧 4MB,留出余量(同 <c>skills.installZip</c>)。更大的包走市场安装(设备自己下载,≤ 20MB)。</summary>
    public const int MaxWireZipBytes = 3 * 1024 * 1024;

    /// <summary>包里每种语言都必须带的 l10n 文件(只带一种语言的插件装上之后,另一种语言下它的每个按钮都会显示成 key 本身)。</summary>
    public static readonly IReadOnlyList<string> LocaleFiles = ["zh-CN.json", "en-US.json"];
}

public sealed record InstalledPackage(PluginManifest Manifest, EnvPath Directory, string? Sha256);

/// <summary>
/// 插件安装(2.x <c>main/plugin/installer.ts</c>):zip / 目录两条来源,<b>同一组防线</b>,全部经 <see cref="IExecutionEnvironment"/> 落盘。
/// <list type="number">
/// <item><b>体积</b>:包 ≤ 20MB、条目 ≤ 2000、展开 ≤ 50MB、深度 ≤ 12;头里写的大小不可信,写盘时边读边计数,超出当场中止(zip bomb)。</item>
/// <item><b>路径</b>:拒绝 <c>..</c>、绝对路径、盘符、反斜杠、空段、大小写不敏感的重复条目、符号链接(Unix mode 0120000);恰好一个顶层目录,名字 = 清单的 <c>publisher.name</c>。</item>
/// <item><b>清单</b>:先只读 <c>&lt;顶层&gt;/package.json</c>(≤ 256KB)并过完整校验;<c>main</c> / icon / l10n 两种语言 / skills(连 <c>SKILL.md</c>)必须真的在包里。</item>
/// <item><b>摘要</b>:市场安装带权威 sha256(服务端给,不采信包里的自述)。</item>
/// <item><b>原子</b>:先解到同级临时目录,旧版本挪成备份再 rename 上位,任何一步失败都还原旧版本并清理。</item>
/// </list>
/// 失败一律是 <see cref="PluginException"/> + 稳定 key。
/// </summary>
public static partial class PluginPackageInstaller
{
    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetter();

    // ───────────────────────── zip ─────────────────────────

    public static async Task<InstalledPackage> InstallZipAsync(
        ReadOnlyMemory<byte> zip, IExecutionEnvironment env, EnvPath root, string? expectedSha256 = null, CancellationToken ct = default)
    {
        if (zip.Length == 0 || zip.Length > PluginLimits.MaxZipBytes)
        {
            throw Fail(PluginErrorKeys.PackageTooLarge, "The package is empty or larger than the allowed size.");
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(zip.Span));
        if (!string.IsNullOrEmpty(expectedSha256) && !string.Equals(expectedSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(PluginErrorKeys.DigestMismatch, "The package digest does not match the one the marketplace vouched for.");
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zip.ToArray(), writable: false), ZipArchiveMode.Read);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            throw Fail(PluginErrorKeys.ZipInvalid, "The file is not a readable ZIP archive.", e);
        }

        using (archive)
        {
            var plan = Plan(archive);
            var manifest = await ReadPlannedManifestAsync(plan).ConfigureAwait(false);
            if (manifest.Id != plan.Top)
            {
                throw Fail(PluginErrorKeys.ZipInvalid, $"The top-level directory \"{plan.Top}\" does not match the manifest id \"{manifest.Id}\".");
            }

            var files = new HashSet<string>(plan.Items.Where(i => !i.IsDirectory).Select(i => i.Relative), StringComparer.Ordinal);
            AssertPackageFiles(manifest, files);
            var target = root.Join(manifest.Id);
            try
            {
                await MaterializeAsync(env, root, target, async staging =>
                {
                    long actual = 0;
                    foreach (var item in plan.Items)
                    {
                        if (item.Relative.Length == 0)
                        {
                            continue;
                        }

                        var destination = staging.Join(item.Relative.Split('/'));
                        if (item.IsDirectory)
                        {
                            await env.Fs.MkdirAsync(destination, recursive: true, ct).ConfigureAwait(false);
                            continue;
                        }

                        var limit = Math.Min(PluginLimits.MaxExpandedBytes - actual, item.Entry!.Length);
                        var input = item.Entry.Open();
                        long written;
                        await using (input.ConfigureAwait(false))
                        {
                            written = await env.Fs.WriteStreamAsync(destination, input, new FsWriteStreamOptions(limit, Exclusive: true, ModeOf(item.Entry), CreateParents: true), ct).ConfigureAwait(false);
                        }

                        if (written != item.Entry.Length)
                        {
                            throw Fail(PluginErrorKeys.ZipInvalid, "A ZIP entry's size does not match its header.");
                        }

                        actual += written;
                    }
                }, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PermissionDenied)
            {
                throw Fail(PluginErrorKeys.DiskPermission, "The install location is not writable.", e);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.TooLarge)
            {
                throw Fail(PluginErrorKeys.PackageTooLarge, "The package expands to more than the allowed size.", e);
            }

            return new InstalledPackage(manifest, target, sha256);
        }
    }

    private sealed record Item(ZipArchiveEntry? Entry, string Relative, bool IsDirectory);

    private sealed record PackagePlan(string Top, IReadOnlyList<Item> Items, ZipArchiveEntry ManifestEntry);

    private static PackagePlan Plan(ZipArchive archive)
    {
        var entries = archive.Entries;
        if (entries.Count == 0 || entries.Count > PluginLimits.MaxEntries)
        {
            throw Fail(PluginErrorKeys.ZipInvalid, "The package has no entries or too many entries.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<Item>(entries.Count);
        string? top = null;
        ZipArchiveEntry? manifestEntry = null;
        long expanded = 0;
        foreach (var entry in entries)
        {
            var isDirectory = entry.FullName.EndsWith('/');
            var name = isDirectory ? entry.FullName[..^1] : entry.FullName;
            if (!SafeEntryName(name))
            {
                throw Fail(PluginErrorKeys.ZipInvalid, "The package contains an unsafe path.");
            }

            var parts = name.Split('/');
            top ??= parts[0];
            if (parts[0] != top)
            {
                throw Fail(PluginErrorKeys.ZipInvalid, "The package must contain exactly one top-level directory.");
            }

            if (!seen.Add(name))
            {
                throw Fail(PluginErrorKeys.ZipInvalid, "The package contains duplicate paths.");
            }

            // Unix mode 0120000 = 符号链接:永不从不可信归档里还原链接
            if ((((entry.ExternalAttributes >>> 16) & 0xFFFF) & 0xF000) == 0xA000)
            {
                throw Fail(PluginErrorKeys.ZipInvalid, "The package must not contain symbolic links.");
            }

            expanded += entry.Length;
            if (expanded > PluginLimits.MaxExpandedBytes)
            {
                throw Fail(PluginErrorKeys.PackageTooLarge, "The package expands to more than the allowed size.");
            }

            var relative = string.Join('/', parts.Skip(1));
            if (relative == "package.json" && !isDirectory)
            {
                manifestEntry = entry;
            }

            items.Add(new Item(isDirectory ? null : entry, relative, isDirectory));
        }

        if (top is null || !IdLike(top))
        {
            throw Fail(PluginErrorKeys.ZipInvalid, "The top-level directory must be named <publisher>.<name>.");
        }

        return new PackagePlan(top, items, manifestEntry ?? throw Fail(PluginErrorKeys.ManifestInvalid, "package.json is missing."));
    }

    private static async Task<PluginManifest> ReadPlannedManifestAsync(PackagePlan plan)
    {
        var input = plan.ManifestEntry.Open();
        await using (input.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            for (;;)
            {
                var read = await input.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > PluginManifestParser.MaxManifestBytes)
                {
                    throw Fail(PluginErrorKeys.ManifestInvalid, "package.json is too large.");
                }

                buffer.Write(chunk, 0, read);
            }

            return ParseOrThrow(Encoding.UTF8.GetString(buffer.ToArray()));
        }
    }

    internal static bool SafeEntryName(string name) =>
        name.Length > 0 && !name.Contains('\\') && !name.StartsWith('/') && !DriveLetter().IsMatch(name) && !name.Contains('\0')
        && name.Split('/') is { } parts && parts.Length <= PluginLimits.MaxDepth && parts.All(p => p.Length > 0 && p != "." && p != "..");

    private static bool IdLike(string name) => PluginManifestParser.IsPluginId(name);

    /// <summary>保留可执行位,但不放开组 / 其他的写权限,也不会带出 setuid 之类;没有 Unix 属性的包用系统默认。</summary>
    private static int? ModeOf(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >>> 16) & 0xFFFF;
        return mode == 0 ? null : (mode & 0x1ED) | 0x180;
    }

    // ───────────────────────── 目录 ─────────────────────────

    /// <summary>
    /// 装一个<b>目录</b>(本地开发 / 从设备上的某个路径装):同样复制一份进插件目录,不做软链(软链意味着插件的代码在开发目录里,而那个目录随时会变成别的东西)。
    /// 开发目录里躺着的 <c>node_modules</c> / <c>.git</c> 不复制(只会是几万个无用文件);其它符号链接一律拒绝。
    /// </summary>
    public static async Task<InstalledPackage> InstallDirectoryAsync(
        IExecutionEnvironment sourceEnvironment, EnvPath sourceDirectory, IExecutionEnvironment env, EnvPath root, CancellationToken ct = default)
    {
        FsEntryInfo info;
        try
        {
            info = await sourceEnvironment.Fs.LStatAsync(sourceDirectory, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            throw Fail(PluginErrorKeys.ManifestInvalid, "The source directory does not exist.", e);
        }

        if (!info.IsDirectory)
        {
            throw Fail(PluginErrorKeys.ManifestInvalid, "The source must be a real directory.");
        }

        var manifest = await ReadManifestAsync(sourceEnvironment, sourceDirectory, ct).ConfigureAwait(false);
        var files = new List<(EnvPath Path, string Relative, long Size)>();
        long total = 0;
        await CollectAsync(sourceEnvironment, sourceDirectory, "", 0, files, size => total += size, () => total, ct).ConfigureAwait(false);
        AssertPackageFiles(manifest, new HashSet<string>(files.Select(f => f.Relative), StringComparer.Ordinal));

        var target = root.Join(manifest.Id);
        try
        {
            await MaterializeAsync(env, root, target, async staging =>
            {
                long actual = 0;
                foreach (var (path, relative, size) in files)
                {
                    var destination = staging.Join(relative.Split('/'));
                    var input = await sourceEnvironment.Fs.OpenReadAsync(path, ct).ConfigureAwait(false);
                    await using (input.ConfigureAwait(false))
                    {
                        actual += await env.Fs.WriteStreamAsync(destination, input, new FsWriteStreamOptions(Math.Min(PluginLimits.MaxExpandedBytes - actual, size), CreateParents: true), ct).ConfigureAwait(false);
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PermissionDenied)
        {
            throw Fail(PluginErrorKeys.DiskPermission, "The install location is not writable.", e);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.TooLarge)
        {
            throw Fail(PluginErrorKeys.PackageTooLarge, "The package expands to more than the allowed size.", e);
        }

        return new InstalledPackage(manifest, target, null);
    }

    private static async Task CollectAsync(
        IExecutionEnvironment env, EnvPath directory, string prefix, int depth, List<(EnvPath, string, long)> files, Action<long> add, Func<long> total, CancellationToken ct)
    {
        if (depth > PluginLimits.MaxDepth)
        {
            throw Fail(PluginErrorKeys.ZipInvalid, "The package directory is nested too deeply.");
        }

        foreach (var entry in await env.Fs.ListAsync(directory, ct).ConfigureAwait(false))
        {
            if (entry.Name is "node_modules" or ".git" || entry.Name.StartsWith(".DS_Store", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.Info.IsSymlink)
            {
                throw Fail(PluginErrorKeys.ZipInvalid, $"The package must not contain symbolic links: {relative}");
            }

            if (entry.Info.IsDirectory)
            {
                await CollectAsync(env, directory.Join(entry.Name), relative, depth + 1, files, add, total, ct).ConfigureAwait(false);
                continue;
            }

            if (!entry.Info.IsFile)
            {
                continue;
            }

            if (files.Count >= PluginLimits.MaxEntries)
            {
                throw Fail(PluginErrorKeys.ZipInvalid, "The package has too many files.");
            }

            add(entry.Info.Size);
            if (total() > PluginLimits.MaxExpandedBytes)
            {
                throw Fail(PluginErrorKeys.PackageTooLarge, "The package expands to more than the allowed size.");
            }

            files.Add((directory.Join(entry.Name), relative, entry.Info.Size));
        }
    }

    // ───────────────────────── 清单 / 包内文件 ─────────────────────────

    /// <summary>读一个已经在盘上的插件目录的清单(装载期用,不写盘)。读不懂 → <see cref="PluginException"/>。</summary>
    public static async Task<PluginManifest> ReadManifestAsync(IExecutionEnvironment env, EnvPath directory, CancellationToken ct = default)
    {
        string text;
        try
        {
            text = await env.Fs.ReadTextAsync(directory.Join("package.json"), PluginManifestParser.MaxManifestBytes, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            throw Fail(PluginErrorKeys.ManifestInvalid, "package.json is missing.", e);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.TooLarge)
        {
            throw Fail(PluginErrorKeys.ManifestInvalid, "package.json is too large.", e);
        }

        return ParseOrThrow(text);
    }

    private static PluginManifest ParseOrThrow(string text)
    {
        var result = PluginManifestParser.Parse(text);
        if (result.Manifest is null)
        {
            var first = result.Errors[0];
            throw new PluginException(
                PluginErrorKeys.ManifestInvalid,
                $"package.json is invalid: {string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}"))}",
                new Dictionary<string, string> { ["field"] = first.Field, ["detail"] = first.Message });
        }

        return result.Manifest;
    }

    /// <summary>
    /// 「清单指到的文件真的在包里吗」:清单解析查<b>形状</b>,这里查<b>存在</b>。只查形状的话,一个 <c>main</c> 指着不存在文件的包会装得干干净净,然后在第一次激活时炸掉。
    /// ★ skill 要连 <c>SKILL.md</c> 一起查;frontmatter 的合法性留给扫描器(唯一权威)。
    /// </summary>
    internal static void AssertPackageFiles(PluginManifest manifest, IReadOnlySet<string> files)
    {
        bool HasFile(string path) => files.Contains(NormalizeRelative(path));
        bool HasDirectory(string path) => files.Any(f => f.StartsWith(NormalizeRelative(path) + "/", StringComparison.Ordinal));

        if (manifest.Kind != PluginKind.WebApp && !HasFile(manifest.Main))
        {
            throw Fail(PluginErrorKeys.PackageFileMissing, $"The entry file \"{manifest.Main}\" is missing from the package.", parameters: new Dictionary<string, string> { ["path"] = NormalizeRelative(manifest.Main) });
        }

        if (manifest.Icon is { } icon && !HasFile(icon))
        {
            throw Fail(PluginErrorKeys.PackageFileMissing, $"The icon \"{icon}\" is missing from the package.", parameters: new Dictionary<string, string> { ["path"] = NormalizeRelative(icon) });
        }

        foreach (var command in manifest.Contributes.Commands)
        {
            if (command.IconFile is { } iconFile && !HasFile(iconFile))
            {
                throw Fail(PluginErrorKeys.PackageFileMissing, $"The icon \"{iconFile}\" of command \"{command.Command}\" is missing from the package.", parameters: new Dictionary<string, string> { ["path"] = NormalizeRelative(iconFile) });
            }
        }

        if (manifest.L10n is { } l10n)
        {
            if (!HasDirectory(l10n))
            {
                throw Fail(PluginErrorKeys.PackageFileMissing, $"The l10n directory \"{l10n}\" is missing.", parameters: new Dictionary<string, string> { ["path"] = NormalizeRelative(l10n) });
            }

            foreach (var file in PluginLimits.LocaleFiles)
            {
                if (!HasFile($"{NormalizeRelative(l10n)}/{file}"))
                {
                    throw Fail(PluginErrorKeys.PackageFileMissing, $"The l10n bundle \"{file}\" is required; ship every supported locale.", parameters: new Dictionary<string, string> { ["path"] = $"{NormalizeRelative(l10n)}/{file}" });
                }
            }
        }

        foreach (var skill in manifest.Contributes.Skills)
        {
            if (!HasDirectory(skill))
            {
                throw Fail(PluginErrorKeys.PackageFileMissing, $"The skill directory \"{skill}\" is missing.", parameters: new Dictionary<string, string> { ["path"] = skill });
            }

            if (!HasFile($"{skill}/SKILL.md"))
            {
                throw Fail(PluginErrorKeys.PackageFileMissing, $"The skill \"{skill}\" has no SKILL.md; the skill scanner would skip it silently.", parameters: new Dictionary<string, string> { ["path"] = $"{skill}/SKILL.md" });
            }
        }
    }

    internal static string NormalizeRelative(string path)
    {
        var p = path;
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.TrimEnd('/');
    }

    // ───────────────────────── 写盘(原子) ─────────────────────────

    /// <summary>staging → backup → rename。<b>失败一定回滚</b>:升级失败之后用户手里应该是旧版本,而不是一个装了一半的目录。</summary>
    private static async Task MaterializeAsync(IExecutionEnvironment env, EnvPath root, EnvPath target, Func<EnvPath, Task> fill, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var staging = root.Join($"{target.FileName}.installing-{suffix}");
        var backup = root.Join($"{target.FileName}.backup-{suffix}");
        var backedUp = false;
        await env.Fs.MkdirAsync(root, recursive: true, ct).ConfigureAwait(false);
        try
        {
            await env.Fs.MkdirAsync(staging, recursive: true, ct).ConfigureAwait(false);
            await fill(staging).ConfigureAwait(false);
            if (await env.Fs.ExistsAsync(target, ct).ConfigureAwait(false))
            {
                await env.Fs.RenameAsync(target, backup, replace: false, ct).ConfigureAwait(false);
                backedUp = true;
            }

            await env.Fs.RenameAsync(staging, target, replace: false, ct).ConfigureAwait(false);
            if (backedUp)
            {
                await TryDeleteAsync(env.Fs, backup).ConfigureAwait(false);
            }
        }
        catch
        {
            await TryDeleteAsync(env.Fs, staging).ConfigureAwait(false);
            if (backedUp)
            {
                try
                {
                    await env.Fs.RenameAsync(backup, target, replace: false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (EnvironmentException)
                {
                    // 还原失败不能盖住真正的错误
                }
            }

            throw;
        }
    }

    private static async Task TryDeleteAsync(IEnvironmentFs fs, EnvPath path)
    {
        try
        {
            await fs.DeleteDirectoryAsync(path, recursive: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (EnvironmentException)
        {
            // 清理失败不致命:留下的 .installing-* / .backup-* 目录下次启动时会被清掉
        }
    }

    private static PluginException Fail(string key, string message, Exception? inner = null, IReadOnlyDictionary<string, string>? parameters = null) => new(key, message, parameters, inner);
}
