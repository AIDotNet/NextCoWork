using System.Collections.Concurrent;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Hooks;

/// <summary>
/// 设置文件里 hooks 段的读写(经 <see cref="IExecutionEnvironment"/>)。读:本机按 mtime+size 缓存(每次工具调用都会读一次;用户在编辑器里手改是预期用法,所以不能读一次就当永远),
/// 远程一条都不进缓存。写:同一份文件的写入串行、<b>读不懂的文件拒绝覆盖</b>(用户点的只是一颗开关,不是「清空我的配置」)、原子替换(临时文件 + 改名)、hooks 以外的键原样保留。
/// </summary>
internal sealed class HookConfigStore
{
    private sealed record Cached(string Key, HookFileContents Contents);

    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    private static string KeyOf(IExecutionEnvironment env, EnvPath path) => $"{env.Id}|{path.Value}";

    public async ValueTask<HookFileContents> ReadAsync(IExecutionEnvironment env, EnvPath path, CancellationToken ct)
    {
        string statKey;
        try
        {
            var info = await env.Fs.StatAsync(path, ct).ConfigureAwait(false);
            statKey = $"{info.ModifiedAt.UtcTicks}:{info.Size}";
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return HookFileContents.Empty; // 没有文件 = 没有钩子,是正常状态
        }

        var local = env.Kind == EnvironmentKind.Local;
        if (local && _cache.TryGetValue(KeyOf(env, path), out var hit) && hit.Key == statKey)
        {
            return hit.Contents;
        }

        string text;
        try
        {
            text = await env.Fs.ReadTextAsync(path, ct: ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound)
        {
            return HookFileContents.Empty;
        }

        var contents = HookSettingsFile.Parse(text);
        if (local)
        {
            _cache[KeyOf(env, path)] = new Cached(statKey, contents);
        }

        return contents;
    }

    /// <summary>
    /// 读 → 补 id → 变换 → 写回。<paramref name="change"/> 拿到当前列表,返回新列表(也可以抛 <see cref="HookException"/> 中止,文件不动)。
    /// 返回写回之后的列表。
    /// </summary>
    public async ValueTask<IReadOnlyList<HookDefinition>> MutateAsync(
        IExecutionEnvironment env, EnvPath path, Func<string> mint,
        Func<IReadOnlyList<HookDefinition>, IReadOnlyList<HookDefinition>> change, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(KeyOf(env, path), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? text = null;
            try
            {
                text = await env.Fs.ReadTextAsync(path, ct: ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
            {
            }

            var contents = HookSettingsFile.Parse(text);
            if (contents.Unreadable)
            {
                throw new HookException(HookErrorKeys.FileUnreadable, "The settings file cannot be parsed; refusing to overwrite it.", new Dictionary<string, string> { ["path"] = path.Value });
            }

            var updated = change(HookSettingsFile.AssignIds(contents.Hooks, mint));
            var rendered = HookSettingsFile.Render(text, updated);
            try
            {
                using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rendered));
                await env.Fs.WriteStreamAsync(path, source, new FsWriteStreamOptions(4 * 1024 * 1024, CreateParents: true), ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code != EnvironmentErrorCode.Cancelled)
            {
                throw new HookException(HookErrorKeys.WriteFailed, $"Could not write the settings file: {e.Message}", new Dictionary<string, string> { ["path"] = path.Value });
            }

            _cache.TryRemove(KeyOf(env, path), out _);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>读一层顺便把手写条目缺的 id 补上并回写(幂等:补完之后再读就不会再写了)。写不进去也让用户看见这些钩子,只是它们还没有稳定 id。</summary>
    public async ValueTask<HookFileContents> ReadAssigningIdsAsync(IExecutionEnvironment env, EnvPath path, Func<string> mint, ILoopDiagnostics log, CancellationToken ct)
    {
        var contents = await ReadAsync(env, path, ct).ConfigureAwait(false);
        if (contents.Unreadable || !HookSettingsFile.HasMissingIds(contents.Hooks))
        {
            return contents;
        }

        try
        {
            var written = await MutateAsync(env, path, mint, static hooks => hooks, ct).ConfigureAwait(false);
            return contents with { Hooks = written };
        }
        catch (HookException e)
        {
            log.Warn($"Could not assign ids to the hooks in {path.Value}: {e.Key}");
            return contents;
        }
    }
}
