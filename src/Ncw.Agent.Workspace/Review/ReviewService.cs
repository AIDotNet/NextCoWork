using System.Security.Cryptography;
using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Review;

/// <summary>
/// 改动审查的撤销 / 重做 / 预检(移植自 2.x <c>ipc/review.ts</c>)。
/// <list type="bullet">
/// <item><b>撤销前先比对</b>:磁盘现状的 SHA-256 必须等于记录里「改动后」的 hash;不等说明这个文件在那之后又被改过(后续的 Run 或用户手改),
/// 非 force 的撤销把它标 <see cref="ReviewFileStatus.Conflict"/> 跳过,由界面提示用户确认后带 force 重发。重做是 force 语义(磁盘此刻应当是 before,直接回写 after)。</item>
/// <item>超限(内容没入库)和项目外的文件禁撤销 / 重做(<see cref="ReviewFileStatus.Skipped"/>)。路径只信记录里的项目相对路径,经路径守卫解析后必须仍在项目根内
/// (符号链接指向根外、<c>..</c>、被篡改成别处的路径一律当项目外跳过,绝不碰)。</item>
/// <item>文件的读、写、删除全部经项目的 <see cref="IExecutionEnvironment"/>;写回是同目录临时文件 + 原子替换,保存中断不会留下被截断的文件。撤销一个新建的文件 = 删除它(内容还在改动集里,重做可以恢复)。</item>
/// <item>有任何文件真的回写了才翻转整轮的撤销态并记时间(<see cref="IFileChangeStore.SetState"/>);一个文件都没动,整轮状态不变。</item>
/// </list>
/// 同一个服务实例上的撤销 / 重做互斥(一次只有一个在改磁盘)。
/// </summary>
public sealed class ReviewService(IFileChangeStore store, TimeProvider? clock = null)
{
    /// <summary>读「磁盘现状」的文本上限(记录里的非超限文件都不超过 1 MiB,留一点余量给后来被改大的)。</summary>
    private const long ReadLimit = 2 * 1024 * 1024;

    /// <summary>写回的上限(同工具层的写入上限量级)。</summary>
    private const long WriteLimit = 16 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _mutation = new(1, 1);

    /// <summary>Run 收尾时落库(顶层 Run 的 id 聚合;子代理 Run 的改动并进父 Run,<paramref name="sourceRunId"/> 是真正写它的 Run)。空改动不落盘。</summary>
    public void RecordRun(string rootRunId, string sourceRunId, string sessionId, string workspaceId, long at, IReadOnlyList<RecordedChange> changes)
    {
        if (changes.Count > 0)
        {
            store.Save(rootRunId, sourceRunId, sessionId, workspaceId, changes, at);
        }
    }

    public ReviewChangeSet? ChangeSet(string runId) => store.Get(runId);

    public IReadOnlyList<ReviewChangeSet> List(string sessionId) => store.ListBySession(sessionId);

    public ReviewFileDiff? FileDiff(string runId, string path) => store.GetDiff(runId, path);

    /// <summary>撤销前的预检:磁盘现状已和记录不符的文件路径(超限、项目外的不算)。</summary>
    public async Task<IReadOnlyList<string>> PrecheckUndoAsync(string runId, IExecutionEnvironment environment, EnvPath root, CancellationToken ct = default)
    {
        if (store.Get(runId) is null)
        {
            return [];
        }

        var conflicts = new List<string>();
        foreach (var snapshot in store.ListSnapshots(runId))
        {
            if (snapshot.Oversize || !snapshot.InWorkspace)
            {
                continue;
            }

            if (await ResolveAsync(environment, root, snapshot.Path, ct).ConfigureAwait(false) is not { } target)
            {
                continue; // 路径越界:撤销时会被跳过,不算冲突
            }

            var current = await ReadCurrentAsync(environment, target, ct).ConfigureAwait(false);
            if (IsUndoConflict(snapshot, current))
            {
                conflicts.Add(snapshot.Path);
            }
        }

        return conflicts;
    }

    public Task<ReviewMutation> UndoAsync(string runId, IExecutionEnvironment environment, EnvPath root, bool force, CancellationToken ct = default) =>
        ApplyAsync(runId, environment, root, undo: true, force, ct);

    /// <summary>恢复(重做)一个已撤销的变更集:磁盘此刻应当是 before,直接回写 after(force 语义)。</summary>
    public Task<ReviewMutation> RedoAsync(string runId, IExecutionEnvironment environment, EnvPath root, CancellationToken ct = default) =>
        ApplyAsync(runId, environment, root, undo: false, force: true, ct);

    private async Task<ReviewMutation> ApplyAsync(string runId, IExecutionEnvironment environment, EnvPath root, bool undo, bool force, CancellationToken ct)
    {
        var set = store.Get(runId);
        if (set is null)
        {
            return new ReviewMutation(runId, ReviewState.Applied, []);
        }

        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = new List<ReviewFileResult>();
            foreach (var snapshot in store.ListSnapshots(runId))
            {
                files.Add(await ApplyOneAsync(snapshot, environment, root, undo, force, ct).ConfigureAwait(false));
            }

            var next = undo ? ReviewState.Reverted : ReviewState.Applied;
            var changed = files.Any(f => f.Status == ReviewFileStatus.Ok);
            if (changed)
            {
                store.SetState(runId, next, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            }

            return new ReviewMutation(runId, changed ? next : set.State, files);
        }
        finally
        {
            _mutation.Release();
        }
    }

    private async Task<ReviewFileResult> ApplyOneAsync(ReviewSnapshot snapshot, IExecutionEnvironment environment, EnvPath root, bool undo, bool force, CancellationToken ct)
    {
        if (snapshot.Oversize)
        {
            return new ReviewFileResult(snapshot.Path, ReviewFileStatus.Skipped, ReviewSkipReason.Oversize);
        }

        if (!snapshot.InWorkspace)
        {
            return new ReviewFileResult(snapshot.Path, ReviewFileStatus.Skipped, ReviewSkipReason.OutsideWorkspace);
        }

        try
        {
            if (await ResolveAsync(environment, root, snapshot.Path, ct).ConfigureAwait(false) is not { } target)
            {
                return new ReviewFileResult(snapshot.Path, ReviewFileStatus.Skipped, ReviewSkipReason.OutsideWorkspace);
            }

            var current = await ReadCurrentAsync(environment, target, ct).ConfigureAwait(false);
            if (!force && IsUndoConflict(snapshot, current))
            {
                return new ReviewFileResult(snapshot.Path, ReviewFileStatus.Conflict);
            }

            // 撤销回到 before(新建文件的 before 为 null → 删除);恢复回到 after(已删除的文件恢复 = 再删一次)。
            var content = undo ? snapshot.Before : snapshot.Kind == ReviewChangeKind.Deleted ? null : snapshot.After;
            await RestoreAsync(environment, target, current, content, ct).ConfigureAwait(false);
            return new ReviewFileResult(snapshot.Path, ReviewFileStatus.Ok);
        }
        catch (EnvironmentException e)
        {
            // 单个文件的失败(权限、是个目录、连接中断……)只标这一个条目;诊断里不带路径。
            return new ReviewFileResult(snapshot.Path, ReviewFileStatus.Error, Error: $"environment:{e.Code}");
        }
    }

    /// <summary>
    /// 撤销时磁盘现状是否和「我们要从中离开的那个状态」不符:现状必须是 after(hash 相同)。文件没了、不是文本、读不了都算不符。
    /// </summary>
    private static bool IsUndoConflict(ReviewSnapshot snapshot, CurrentFile current)
    {
        if (snapshot.Kind == ReviewChangeKind.Deleted)
        {
            return current.State != FileState.Absent; // 已被删除的文件,现状应当是「不存在」
        }

        return current.State != FileState.Text || Sha256(current.Text!) != snapshot.AfterHash;
    }

    private static async Task RestoreAsync(IExecutionEnvironment environment, EnvPath target, CurrentFile current, string? content, CancellationToken ct)
    {
        var fs = environment.Fs;
        if (content is null)
        {
            // 让它不存在:已经不在了当作成功
            if (current.State != FileState.Absent)
            {
                try
                {
                    await fs.DeleteFileAsync(target, ct).ConfigureAwait(false);
                }
                catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.NotFound)
                {
                }
            }

            return;
        }

        // 保留现有文件的 BOM 和权限位;新建(撤销了一次新建之后的重做)用系统默认
        var bytes = EncodeText(content, current.HasBom);
        await fs.WriteStreamAsync(
            target, new MemoryStream(bytes, writable: false),
            new FsWriteStreamOptions(WriteLimit, UnixMode: current.UnixMode is > 0 ? current.UnixMode : null, CreateParents: true), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 项目相对路径 → 环境里的规范绝对路径;落在项目根之外(符号链接、<c>..</c>、绝对路径)返回 null。只信路径守卫,绝不碰根外的东西。
    /// </summary>
    private static async Task<EnvPath?> ResolveAsync(IExecutionEnvironment environment, EnvPath root, string path, CancellationToken ct)
    {
        try
        {
            return await environment.Paths.ResolveWithinAsync(root, path, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.PathEscape or EnvironmentErrorCode.InvalidPath)
        {
            return null;
        }
    }

    private static async Task<CurrentFile> ReadCurrentAsync(IExecutionEnvironment environment, EnvPath target, CancellationToken ct)
    {
        try
        {
            var info = await environment.Fs.StatAsync(target, ct).ConfigureAwait(false);
            if (!info.IsFile)
            {
                return CurrentFile.Unreadable;
            }

            var bytes = await environment.Fs.ReadBytesAsync(target, ReadLimit, ct).ConfigureAwait(false);
            var bom = TextNewlines.HasUtf8Bom(bytes);
            return new CurrentFile(FileState.Text, StrictUtf8.GetString(bom ? bytes.AsSpan(3) : bytes), bom, info.UnixMode);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return CurrentFile.Absent;
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.TooLarge or EnvironmentErrorCode.IsDirectory or EnvironmentErrorCode.PermissionDenied)
        {
            return CurrentFile.Unreadable;
        }
        catch (DecoderFallbackException)
        {
            return CurrentFile.Unreadable; // 不是合法的 UTF-8:当作和记录不符
        }
    }

    private static byte[] EncodeText(string text, bool bom)
    {
        var body = Encoding.UTF8.GetBytes(text);
        if (!bom)
        {
            return body;
        }

        var withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        body.CopyTo(withBom, 3);
        return withBom;
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private enum FileState
    {
        Absent,
        Text,
        Unreadable,
    }

    private sealed record CurrentFile(FileState State, string? Text = null, bool HasBom = false, int UnixMode = 0)
    {
        public static readonly CurrentFile Absent = new(FileState.Absent);

        public static readonly CurrentFile Unreadable = new(FileState.Unreadable);
    }
}
