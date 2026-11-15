using Ncw.Agent.Environments;

namespace Ncw.Agent.Skills.Install;

/// <summary>
/// 启动时把内置 Skill 从随应用发布的资源目录铺到全局 Skill 根(2.x <c>kernel/skill/bundled.ts</c>)。
/// <para>
/// ★ <b>内置 Skill 每次启动都覆盖刷新</b>(改了资源里的内容,下次启动即生效),但<b>绝不动用户自己装的同名 Skill</b>——靠 <see cref="SkillLoader.BundledMarker"/> 哨兵区分:
/// 目标不存在 → 安装并打哨兵;目标存在且带哨兵(我们上次铺的)→ 覆盖刷新;目标存在但不带哨兵(用户的同名 Skill)→ 跳过。哨兵是空文件,加载器忽略它(远端资产判定也不把它算资产,
/// 否则每一条内置 Skill 在 SSH 会话里都会被拒掉)。
/// </para>
/// 覆盖是原子的:新副本先在暂存目录里备好,旧目标挪进暂存目录当备份,再把新副本 rename 到目标;任一步失败就把备份挪回来,发现不了半个 Skill。符号链接一律不拷。
/// 失败变诊断,启动继续。源与目标都经 <see cref="IExecutionEnvironment"/>(都在设备本机,但本层不直接碰 <c>System.IO</c>)。
/// </summary>
public static class BundledSkills
{
    public static async Task<IReadOnlyList<SkillDiagnostic>> InstallAsync(
        IExecutionEnvironment sourceEnvironment, EnvPath sourceRoot, IExecutionEnvironment targetEnvironment, EnvPath targetRoot, CancellationToken ct = default)
    {
        var diagnostics = new List<SkillDiagnostic>();
        IReadOnlyList<FsDirEntry> entries;
        try
        {
            entries = await sourceEnvironment.Fs.ListAsync(sourceRoot, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (!SkillLoader.IsConnection(e))
        {
            return [new SkillDiagnostic(sourceRoot.Value, SkillDiagnosticKeys.BundledFailed, $"Could not read the bundled Skills directory: {e.Message}")];
        }

        var source = sourceEnvironment.Fs;
        var target = targetEnvironment.Fs;
        foreach (var entry in entries)
        {
            if (!entry.Info.IsDirectory || !SkillLimits.IsValidName(entry.Name))
            {
                continue;
            }

            var from = sourceRoot.Join(entry.Name);
            var to = targetRoot.Join(entry.Name);
            EnvPath? staging = null;
            var movedBackup = false;
            try
            {
                var exists = await target.ExistsAsync(to, ct).ConfigureAwait(false);
                // 目标已存在但没有哨兵 → 是用户装的同名 Skill,不是我们的内置版,别碰
                if (exists && !await target.ExistsAsync(to.Join(SkillLoader.BundledMarker), ct).ConfigureAwait(false))
                {
                    continue;
                }

                if (!await IsFileAsync(source, from.Join("SKILL.md"), ct).ConfigureAwait(false))
                {
                    diagnostics.Add(new SkillDiagnostic(from.Value, SkillDiagnosticKeys.BundledInvalid, "A bundled Skill is missing SKILL.md.", SkillLoader.Params(("name", entry.Name))));
                    continue;
                }

                await target.MkdirAsync(targetRoot, recursive: true, ct).ConfigureAwait(false);
                var stagingPath = targetRoot.Join($".bundled-{entry.Name}-{Guid.NewGuid().ToString("N")[..12]}");
                staging = stagingPath;
                var stagedPackage = stagingPath.Join(entry.Name);
                await CopyTreeAsync(source, from, target, stagedPackage, ct).ConfigureAwait(false);
                await target.WriteTextAsync(stagedPackage.Join(SkillLoader.BundledMarker), "", new FsWriteOptions(), ct).ConfigureAwait(false);
                if (exists)
                {
                    // 旧目标挪进暂存目录当备份(同一文件系统,rename 才是原子的)
                    await target.RenameAsync(to, stagingPath.Join(".old"), replace: false, ct).ConfigureAwait(false);
                    movedBackup = true;
                }

                await target.RenameAsync(stagedPackage, to, replace: false, ct).ConfigureAwait(false);
                movedBackup = false; // 新副本已就位,备份不再需要回滚
            }
            catch (EnvironmentException e) when (!SkillLoader.IsConnection(e))
            {
                // 回滚:旧目标挪走了、新的没上去 → 把备份挪回来,别留下一个空洞
                if (movedBackup && staging is { } s && !await target.ExistsAsync(to, CancellationToken.None).ConfigureAwait(false))
                {
                    try
                    {
                        await target.RenameAsync(s.Join(".old"), to, replace: false, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (EnvironmentException rollback)
                    {
                        diagnostics.Add(new SkillDiagnostic(to.Value, SkillDiagnosticKeys.BundledFailed, rollback.Message, SkillLoader.Params(("name", entry.Name))));
                    }
                }

                diagnostics.Add(new SkillDiagnostic(to.Value, SkillDiagnosticKeys.BundledFailed, e.Message, SkillLoader.Params(("name", entry.Name))));
            }
            finally
            {
                if (staging is { } s)
                {
                    try
                    {
                        await target.DeleteDirectoryAsync(s, recursive: true, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (EnvironmentException e)
                    {
                        diagnostics.Add(new SkillDiagnostic(s.Value, SkillDiagnosticKeys.BundledFailed, e.Message));
                    }
                }
            }
        }

        return diagnostics;
    }

    private static async ValueTask<bool> IsFileAsync(IEnvironmentFs fs, EnvPath path, CancellationToken ct)
    {
        try
        {
            return (await fs.LStatAsync(path, ct).ConfigureAwait(false)).IsFile;
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return false;
        }
    }

    private static async Task CopyTreeAsync(IEnvironmentFs source, EnvPath from, IEnvironmentFs target, EnvPath to, CancellationToken ct)
    {
        await target.MkdirAsync(to, recursive: true, ct).ConfigureAwait(false);
        foreach (var entry in await source.ListAsync(from, ct).ConfigureAwait(false))
        {
            var child = from.Join(entry.Name);
            var destination = to.Join(entry.Name);
            if (entry.Info.IsDirectory)
            {
                await CopyTreeAsync(source, child, target, destination, ct).ConfigureAwait(false);
            }
            else if (entry.Info.IsFile)
            {
                var input = await source.OpenReadAsync(child, ct).ConfigureAwait(false);
                await using (input.ConfigureAwait(false))
                {
                    await target.WriteStreamAsync(destination, input, new FsWriteStreamOptions(SkillLimits.MaxExpandedBytes, Exclusive: true, entry.Info.UnixMode == 0 ? null : entry.Info.UnixMode), ct).ConfigureAwait(false);
                }
            }

            // 符号链接和特殊文件不拷
        }
    }
}
