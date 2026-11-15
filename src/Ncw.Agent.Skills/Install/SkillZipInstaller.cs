using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Environments;
using Ncw.Core.Text;

namespace Ncw.Agent.Skills.Install;

/// <summary>安装结果。<see cref="Target"/> 是安装目录(执行环境里的路径)。</summary>
public sealed record InstalledSkill(string Name, string? Version, string Sha256, EnvPath Target);

/// <summary>
/// 从 zip 字节安装一条 Skill(2.x <c>kernel/skill/install.ts</c>)。zip 来自市场或用户选的文件,是<b>不可信输入</b>;全部校验在写盘之前,写盘经 <see cref="IExecutionEnvironment"/>。
/// <list type="number">
/// <item><b>体积</b>:包本身 ≤ 20MB;条目数 ≤ 1000;展开后总量 ≤ 50MB——头里写的大小不可信,所以写盘时<b>边读边计数</b>,超出当场中止(zip bomb)。</item>
/// <item><b>路径</b>:拒绝 <c>..</c>、绝对路径、盘符、反斜杠、空段、深度 &gt; 12、大小写不敏感的重复条目;拒绝符号链接条目(哪怕目标看起来在根内,也不把不可信包里的链接落到磁盘上);开头的 <c>./</c> 容忍。</item>
/// <item><b>顶层</b>:<c>SKILL.md</c> 在根(扁平包,目录名取自 frontmatter 的 name,绝不取自可被用户改名的下载文件名)或者恰好一个顶层目录(名字合法,且等于 name);保留的元数据文件名不许出现。</item>
/// <item><b>SKILL.md</b>:≤ 256KB、frontmatter 一行都不能被跳过(安装器把 skipped 非空当硬失败)、name / description / 正文合法。</item>
/// <item><b>原子</b>:先解到同级临时目录,旧版本挪成备份再 rename 上位,任何一步失败都还原旧版本并清理。</item>
/// </list>
/// 错误一律是 <see cref="SkillException"/> + 稳定 key;不含用户路径以外的信息。
/// </summary>
public static partial class SkillZipInstaller
{
    public static async Task<InstalledSkill> InstallAsync(
        ReadOnlyMemory<byte> zip, IExecutionEnvironment env, EnvPath root, string? expectedSha256 = null, string? fileNameHint = null, CancellationToken ct = default)
    {
        if (zip.Length > SkillLimits.MaxZipBytes)
        {
            throw Fail(SkillErrorKeys.PackageTooLarge, "The ZIP file is too large.");
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(zip.Span));
        if (!string.IsNullOrEmpty(expectedSha256) && !string.Equals(expectedSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(SkillErrorKeys.DigestMismatch, "The ZIP digest does not match.");
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zip.ToArray(), writable: false), ZipArchiveMode.Read);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            throw Fail(SkillErrorKeys.ZipInvalid, "The file is not a readable ZIP archive.", e);
        }

        using (archive)
        {
            var plan = Plan(archive);
            var (name, version) = await ValidateSkillMdAsync(plan, fileNameHint).ConfigureAwait(false);
            try
            {
                return await WriteAsync(plan, name, version, sha256, env, root, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PermissionDenied)
            {
                throw Fail(SkillErrorKeys.DiskPermission, "The install location is not writable.", e);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.TooLarge)
            {
                throw Fail(SkillErrorKeys.PackageTooLarge, "The ZIP expands to more than the allowed size.", e);
            }
        }
    }

    // ───────────────────────── 校验 ─────────────────────────

    private sealed record Item(ZipArchiveEntry Entry, string Path, bool IsDirectory);

    private sealed record InstallPlan(IReadOnlyList<Item> Items, string? PackageRoot, Item SkillMd);

    /// <summary>规范化一个条目路径;不合法返回 null。</summary>
    internal static string? SafeEntryPath(string path)
    {
        var name = path.EndsWith('/') ? path[..^1] : path;
        if (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        if (name.Length == 0 || name.Contains('\\') || name.StartsWith('/') || DriveLetter().IsMatch(name))
        {
            return null;
        }

        var parts = name.Split('/');
        return parts.Length <= SkillLimits.MaxDepth && parts.All(p => p.Length > 0 && p != "." && p != "..") ? name : null;
    }

    private static InstallPlan Plan(ZipArchive archive)
    {
        var entries = archive.Entries;
        if (entries.Count == 0 || entries.Count > SkillLimits.MaxEntries)
        {
            throw Fail(SkillErrorKeys.ZipInvalid, "The ZIP has no entries or too many entries.");
        }

        var items = new List<Item>(entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in entries)
        {
            var path = SafeEntryPath(entry.FullName) ?? throw Fail(SkillErrorKeys.ZipInvalid, "The ZIP contains an illegal path.");
            if (!seen.Add(path))
            {
                throw Fail(SkillErrorKeys.ZipInvalid, "The ZIP contains a duplicate path.");
            }

            // Unix mode 0120000 = 符号链接。不可信的包里的链接一律不落盘。
            if ((((entry.ExternalAttributes >>> 16) & 0xFFFF) & 0xF000) == 0xA000)
            {
                throw Fail(SkillErrorKeys.ZipInvalid, "The ZIP contains a symbolic link.");
            }

            expanded += entry.Length;
            if (expanded > SkillLimits.MaxExpandedBytes)
            {
                throw Fail(SkillErrorKeys.PackageTooLarge, "The ZIP expands to more than the allowed size.");
            }

            items.Add(new Item(entry, path, entry.FullName.EndsWith('/')));
        }

        // 扁平包:SKILL.md 在根;否则必须恰好一个顶层目录。
        string? packageRoot = null;
        if (!items.Any(i => i.Path == "SKILL.md" && !i.IsDirectory))
        {
            var tops = items.Select(i => i.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
            if (tops.Count != 1)
            {
                throw Fail(SkillErrorKeys.ZipInvalid, "The ZIP must have exactly one top-level directory.");
            }

            packageRoot = tops[0];
            if (!SkillLimits.IsValidName(packageRoot))
            {
                throw Fail(SkillErrorKeys.ZipInvalid, "The top-level directory name is not a valid Skill name.");
            }
        }

        var skillPath = packageRoot is null ? "SKILL.md" : $"{packageRoot}/SKILL.md";
        var skillMd = items.FirstOrDefault(i => i.Path == skillPath && !i.IsDirectory)
            ?? throw Fail(SkillErrorKeys.ZipInvalid, "The ZIP has no SKILL.md at the expected location.");
        var metadataPath = packageRoot is null ? SkillLoader.PackageMetadataFile : $"{packageRoot}/{SkillLoader.PackageMetadataFile}";
        if (items.Any(i => i.Path == metadataPath))
        {
            throw Fail(SkillErrorKeys.ZipInvalid, "The ZIP uses a reserved metadata file name.");
        }

        return new InstallPlan(items, packageRoot, skillMd);
    }

    private static async Task<(string Name, string? Version)> ValidateSkillMdAsync(InstallPlan plan, string? fileNameHint)
    {
        var bytes = await ReadBoundedAsync(plan.SkillMd.Entry, SkillLimits.FileMaxBytes).ConfigureAwait(false)
            ?? throw Fail(SkillErrorKeys.SkillMdInvalid, "SKILL.md is too large.");
        var fm = FrontmatterParser.Parse(Encoding.UTF8.GetString(bytes));
        if (fm.Skipped.Count > 0)
        {
            throw Fail(SkillErrorKeys.SkillMdInvalid, "The SKILL.md frontmatter uses syntax that cannot be read.");
        }

        var name = FrontmatterParser.String(fm, "name");
        var description = FrontmatterParser.String(fm, "description");
        if (string.IsNullOrEmpty(name)
            || (plan.PackageRoot is not null && name != plan.PackageRoot)
            || !SkillLimits.IsValidName(name)
            || string.IsNullOrEmpty(description)
            || description.Length > SkillLimits.DescriptionMax
            || fm.Body.Trim().Length == 0
            || fm.Body.Length > SkillLimits.BodyMax)
        {
            throw Fail(SkillErrorKeys.SkillMdInvalid, "The SKILL.md metadata is not valid.");
        }

        // 版本号三个来源,由强到弱:顶层 version、metadata: 下的 version(市场上的包多半写在这儿)、最后才是文件名——文件名是用户能随手改的。
        var version = FrontmatterParser.String(fm, "version") ?? FrontmatterParser.String(fm, "metadata.version")
            ?? (fileNameHint is null ? null : VersionInName().Match(fileNameHint) is { Success: true } m ? m.Groups[1].Value : null);
        return (name, version);
    }

    private static async Task<byte[]?> ReadBoundedAsync(ZipArchiveEntry entry, int limit)
    {
        var stream = entry.Open();
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            for (;;)
            {
                var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > limit)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
    }

    // ───────────────────────── 写盘(原子) ─────────────────────────

    private static async Task<InstalledSkill> WriteAsync(
        InstallPlan plan, string name, string? version, string sha256, IExecutionEnvironment env, EnvPath root, CancellationToken ct)
    {
        var fs = env.Fs;
        var target = root.Join(name);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var staging = root.Join($"{name}.installing-{suffix}");
        var backup = root.Join($"{name}.backup-{suffix}");
        var backedUp = false;
        await fs.MkdirAsync(root, recursive: true, ct).ConfigureAwait(false);
        try
        {
            long actual = 0;
            foreach (var item in plan.Items)
            {
                var child = plan.PackageRoot is null ? item.Path : string.Join('/', item.Path.Split('/').Skip(1));
                if (child.Length == 0)
                {
                    continue;
                }

                var destination = staging.Join(child.Split('/'));
                if (item.IsDirectory)
                {
                    await fs.MkdirAsync(destination, recursive: true, ct).ConfigureAwait(false);
                    continue;
                }

                // 头里的大小不可信:每个条目最多写「剩余预算」与「声明大小」中较小的那个,超出当场中止
                var limit = Math.Min(SkillLimits.MaxExpandedBytes - actual, item.Entry.Length);
                var input = item.Entry.Open();
                long written;
                await using (input.ConfigureAwait(false))
                {
                    written = await fs.WriteStreamAsync(destination, input, new FsWriteStreamOptions(limit, Exclusive: true, ModeOf(item.Entry), CreateParents: true), ct).ConfigureAwait(false);
                }

                if (written != item.Entry.Length)
                {
                    throw Fail(SkillErrorKeys.ZipInvalid, "A ZIP entry's size does not match its header.");
                }

                actual += written;
            }

            await fs.WriteTextAsync(staging.Join(SkillLoader.PackageMetadataFile), MetadataJson(sha256, version), new FsWriteOptions(Exclusive: true, CreateParents: true), ct).ConfigureAwait(false);
            if (await fs.ExistsAsync(target, ct).ConfigureAwait(false))
            {
                await fs.RenameAsync(target, backup, replace: false, ct).ConfigureAwait(false);
                backedUp = true;
            }

            await fs.RenameAsync(staging, target, replace: false, ct).ConfigureAwait(false);
            if (backedUp)
            {
                await TryDeleteAsync(fs, backup).ConfigureAwait(false);
            }

            return new InstalledSkill(name, version, sha256, target);
        }
        catch
        {
            await TryDeleteAsync(fs, staging).ConfigureAwait(false);
            if (backedUp)
            {
                // 激活失败时把旧版本放回去
                try
                {
                    await fs.RenameAsync(backup, target, replace: false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (EnvironmentException)
                {
                    // 还原失败不能盖住真正的错误
                }
            }

            throw;
        }
    }

    /// <summary>保留可执行位(scripts/*.sh 装上之后要能跑),但不放开组 / 其他的写权限,也不会带出 setuid 之类;没有 Unix 属性的包用系统默认。</summary>
    private static int? ModeOf(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >>> 16) & 0xFFFF;
        return mode == 0 ? null : (mode & 0x1ED) | 0x180; // rwxr-xr-x 的子集 + 属主可读写
    }

    private static string MetadataJson(string sha256, string? version)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceKind", "zip");
            writer.WriteString("sha256", sha256);
            if (!string.IsNullOrEmpty(version))
            {
                writer.WriteString("version", version);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task TryDeleteAsync(IEnvironmentFs fs, EnvPath path)
    {
        try
        {
            await fs.DeleteDirectoryAsync(path, recursive: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (EnvironmentException)
        {
            // 清理失败不致命(留下一个 .installing-* 目录,下次扫描因为目录名不合法而跳过)
        }
    }

    private static SkillException Fail(string key, string message, Exception? inner = null) => new(key, message, null, inner);

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetter();

    [GeneratedRegex(@"v?(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionInName();
}
