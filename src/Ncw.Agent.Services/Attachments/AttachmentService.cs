using System.Security.Cryptography;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;
using Ncw.Core.Ids;

namespace Ncw.Agent.Services.Attachments;

public enum AttachmentFailure
{
    Invalid,
    NotFound,
    TooLarge,
    Conflict,
}

/// <summary>附件的业务失败。<see cref="Key"/> 是稳定的 <c>attachments.*</c> key(2.x 的 <c>attachment.error.*</c>,控制端翻译),<see cref="Params"/> 是翻译用的参数(文件名、上限……)。</summary>
public sealed class AttachmentException(AttachmentFailure kind, string key, string message, IReadOnlyDictionary<string, string>? parameters = null) : Exception(message)
{
    public AttachmentFailure Kind { get; } = kind;

    public string Key { get; } = key;

    public IReadOnlyDictionary<string, string> Params { get; } = parameters ?? new Dictionary<string, string>();
}

/// <summary>对外的附件。★ <see cref="Url"/> 是 <c>ncw://</c> 引用,<b>绝对路径不出设备</b>(给路径的话渲染层除了拼字符串没有用途,目录结构变成跨进程契约,转录导出时还漂到别的机器上)。</summary>
public sealed record AttachmentInfo(string Id, string Scope, string? OwnerId, string DisplayName, string Mime, long Size, string? Checksum, long CreatedAt, string Url);

public sealed record AttachmentChunk(byte[] Data, long Total, string Mime, bool Eof);

/// <summary>
/// 附件(2.x 的 <c>services/attachments.ts</c> + <c>ipc/attachment.ts</c>):分块上传 → 落盘 → 去重 → 登记草稿;移除;列出草稿;按引用读取;复制 / 分支时的图片搬家;删会话时回收文件。
/// <list type="bullet">
/// <item><b>落盘顺序</b>:<c>.tmp</c> 写完再改名是原子替换:写到一半崩掉留下的是个 <c>.tmp</c> 残片,不是「大小对不上但看起来正常」的目标文件(它的校验和已经进表,下次去重会命中这个坏文件)。临时名以 <c>.</c> 开头:清理按前缀认得出残片,而它不会被 <c>ncw://</c> 寻址到。</item>
/// <item><b>去重限定在同 scope + 同 owner</b>:跨会话共用一个物理文件,删一个会话就会波及另一个。</item>
/// <item><b>图片以字节为准</b>:能识别文件头时以字节为准,否则只接受发送端同一份 MIME 白名单(浏览器把 JPEG 写成 <c>image/jpg</c>、把 PNG 标成 JPEG 都有)。</item>
/// <item><b>文件名绝不进路径</b>:磁盘名是 ULID + 扩展名;原始名只存 <c>display_name</c>。</item>
/// <item><b>只删受管文件</b>:附件 id 永远不能变成任意删除原语;已提交的附件不能通过附件 id 从消息里摘掉(会让转录引用一个不存在的文件)。</item>
/// <item>大文件分块:协议帧上限 4 MiB,单个附件上限 32 MiB,所以上传是 begin / chunk(≤ 1 MiB)/ commit,暂存在 <c>attachments/.uploads/</c>;未提交的 10 分钟过期,同时最多 16 个。</item>
/// </list>
/// </summary>
public sealed class AttachmentService(string dataRoot, TimeProvider clock, AttachmentRepository repository, SessionRepository sessions)
{
    public const int MaxChunkBytes = 1024 * 1024;
    public const int MaxOpenUploads = 16;
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(10);

    private sealed record Upload(string Id, string SessionId, string DisplayName, string Mime, long Total, string Path, DateTimeOffset Expires);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);

    public string Root => Path.Combine(dataRoot, "attachments");

    private string Staging => Path.Combine(Root, ".uploads");

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    // ───────────────────────── 上传 ─────────────────────────

    /// <summary>开始一次上传。<paramref name="sessionId"/> 是归属会话(可以还不存在:用户新建对话、传了图、还没发送)。</summary>
    public string BeginUpload(string sessionId, string displayName, string mime, long totalBytes)
    {
        if (!Attachments_IsSafe(sessionId) || !repository.OwnerVisible(sessionId))
        {
            throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.invalidLocation", "Invalid attachment location.");
        }

        var name = string.IsNullOrWhiteSpace(displayName) ? "attachment" : displayName.Trim();
        if (totalBytes <= 0)
        {
            throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.empty", "Empty attachment.", new Dictionary<string, string> { ["name"] = name });
        }

        if (totalBytes > Core.Domain.Attachments.MaxBytes)
        {
            throw TooLarge(name);
        }

        lock (_gate)
        {
            Sweep();
            if (_uploads.Count >= MaxOpenUploads)
            {
                throw new AttachmentException(AttachmentFailure.Conflict, "attachments.error.tooManyUploads", "Too many uploads are in progress.");
            }

            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            Directory.CreateDirectory(Staging);
            var path = Path.Combine(Staging, id);
            File.WriteAllBytes(path, []);
            _uploads[id] = new Upload(id, sessionId, name, mime, totalBytes, path, clock.GetUtcNow() + UploadLifetime);
            return id;
        }
    }

    private static bool Attachments_IsSafe(string segment) => Core.Domain.Attachments.IsSafeSegment(segment);

    private static AttachmentException TooLarge(string name) => new(
        AttachmentFailure.TooLarge, "attachments.error.tooLarge", "Attachment exceeds the upload limit.",
        new Dictionary<string, string> { ["name"] = name, ["limit"] = (Core.Domain.Attachments.MaxBytes / 1024 / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var expired in _uploads.Values.Where(u => u.Expires <= now).ToList())
        {
            _uploads.Remove(expired.Id);
            TryDelete(expired.Path);
        }
    }

    /// <summary>追加一块。块必须按顺序到(偏移 = 已收到的字节数;重发已收到的块幂等)。返回已收到的字节数。</summary>
    public long WriteChunk(string uploadId, long offset, byte[] data)
    {
        lock (_gate)
        {
            Sweep();
            var upload = Require(uploadId);
            if (data.Length > MaxChunkBytes)
            {
                throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.chunkTooLarge", "A chunk is at most 1 MiB.");
            }

            var have = new FileInfo(upload.Path).Length;
            if (offset > have || (offset < have && offset + data.Length > have))
            {
                throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.chunkOrder", "Chunks must arrive in order.");
            }

            if (offset == have)
            {
                if (have + data.Length > upload.Total)
                {
                    throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.chunkOverflow", "More bytes arrived than were announced.");
                }

                using var stream = new FileStream(upload.Path, FileMode.Append, FileAccess.Write, FileShare.None);
                stream.Write(data);
                have += data.Length;
            }

            return have;
        }
    }

    public void AbortUpload(string uploadId)
    {
        lock (_gate)
        {
            if (_uploads.Remove(uploadId, out var upload))
            {
                TryDelete(upload.Path);
            }
        }
    }

    private Upload Require(string uploadId) =>
        _uploads.TryGetValue(uploadId, out var upload) ? upload : throw new AttachmentException(AttachmentFailure.NotFound, "attachments.error.uploadNotFound", "The upload no longer exists.");

    /// <summary>收尾:校验大小 → 校验 MIME(图片以字节为准)→ 去重 → 原子落盘 → 登记草稿。</summary>
    public AttachmentInfo Commit(string uploadId)
    {
        Upload upload;
        lock (_gate)
        {
            upload = Require(uploadId);
            if (new FileInfo(upload.Path).Length != upload.Total)
            {
                throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.incomplete", "The upload is not complete.");
            }

            _uploads.Remove(uploadId);
        }

        try
        {
            return Store(upload.SessionId, upload.DisplayName, upload.Mime, File.ReadAllBytes(upload.Path));
        }
        finally
        {
            TryDelete(upload.Path);
        }
    }

    /// <summary>落盘 + 去重 + 登记(2.x 的 <c>uploadAttachment</c>;也给会话层生成图片、导入器用)。</summary>
    public AttachmentInfo Store(string ownerSessionId, string displayName, string declaredMime, byte[] bytes)
    {
        const string scope = Core.Domain.Attachments.Session;
        if (bytes.Length == 0)
        {
            throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.empty", "Empty attachment.", new Dictionary<string, string> { ["name"] = displayName });
        }

        if (bytes.Length > Core.Domain.Attachments.MaxBytes)
        {
            throw TooLarge(displayName);
        }

        var mime = declaredMime;
        var inferred = Core.Domain.Attachments.MimeOfExt(displayName);
        if (Core.Domain.Attachments.IsImageMime(mime) || Core.Domain.Attachments.IsImageMime(inferred))
        {
            // 浏览器 / 扩展名可能把 JPEG 写成 image/jpg,也可能把 PNG 标成 JPEG:能识别文件头时以字节为准,否则只接受发送端同一份 MIME 白名单。
            var declared = string.IsNullOrWhiteSpace(mime) || mime == "application/octet-stream" ? inferred : mime;
            mime = Core.Domain.Attachments.ImageMimeOfBytes(bytes) ?? Core.Domain.Attachments.NormalizeImageMime(declared)
                ?? throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.unsupportedImage", $"Unsupported image media type: {declared}", new Dictionary<string, string> { ["mime"] = declared });
        }

        var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // 去重的是字节,但旧记录的类型、落点与大小也必须仍然可用。
        if (repository.FindByChecksum(checksum, scope, ownerSessionId) is { } hit
            && (!Core.Domain.Attachments.IsImageMime(mime) || Core.Domain.Attachments.MimeOfExt(hit.Path) == mime)
            && Core.Domain.Attachments.RelativePath(new AttachmentLocator(scope, ownerSessionId, Path.GetFileName(hit.Path))) is { } hitRel
            && Path.GetFullPath(hit.Path) == Path.GetFullPath(Path.Combine(Root, hitRel))
            && File.Exists(hit.Path) && new FileInfo(hit.Path).Length == bytes.Length)
        {
            return ToInfo(hit) with { DisplayName = displayName };
        }

        var id = Ulid.New(clock);
        var fileName = id + Core.Domain.Attachments.ExtOfMime(mime);
        var locator = new AttachmentLocator(scope, ownerSessionId, fileName);
        var rel = Core.Domain.Attachments.RelativePath(locator) ?? throw Invalid();
        var target = Path.Combine(Root, rel);
        var tmp = Path.Combine(Path.GetDirectoryName(target)!, $".{fileName}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.writeFailed", "Unable to save the attachment.", new Dictionary<string, string> { ["name"] = displayName });
        }

        var createdAt = Now;
        repository.PutDraft(new AttachmentRow(id, scope, ownerSessionId, null, null, target, bytes.Length, checksum, "draft", displayName, createdAt));
        return new AttachmentInfo(id, scope, ownerSessionId, displayName, mime, bytes.Length, checksum, createdAt, Core.Domain.Attachments.BuildUrl(locator) ?? throw Invalid());
    }

    private static AttachmentException Invalid() => new(AttachmentFailure.Invalid, "attachments.error.invalidLocation", "Invalid attachment location.");

    // ───────────────────────── 移除 / 列出 / 读取 ─────────────────────────

    /// <summary>
    /// 移除输入框里的一张待发附件。已提交的行属于某条消息,不能凭 id 摘掉(会让转录引用一个不存在的文件)。
    /// 只删附件根下 <c>sessions/</c> 里的受管文件;外部绝对路径的只是引用,用户的文件从不碰。文件删不掉时保留行以便重试;文件本来就不在 = 已经等于删了。
    /// </summary>
    public void Remove(string id)
    {
        if (repository.Get(id) is not { } row)
        {
            return;
        }

        if (row.Status == "committed" || row.MessageId is not null || row.SessionId is not null)
        {
            throw new AttachmentException(AttachmentFailure.Conflict, "attachments.error.committed", "A committed attachment cannot be removed from its message.");
        }

        if (IsManaged(row.Path) && repository.ReferenceCount(row.Path, row.Id) == 0)
        {
            try
            {
                File.Delete(row.Path); // 符号链接删的是链接本身
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new AttachmentException(AttachmentFailure.Conflict, "attachments.error.deleteFailed", "The attachment file could not be deleted.");
            }
        }

        repository.Remove(id);
    }

    /// <summary>重启后恢复草稿附件区。★ 显示名从 <c>display_name</c> 列读回,不再退化成 ULID。</summary>
    public IReadOnlyList<AttachmentInfo> ListBySession(string sessionId) => [.. repository.ListDrafts(sessionId).Select(ToInfo)];

    /// <summary>
    /// 按 <c>ncw://</c> 引用分块读(控制端显示图片、下载附件用:远程控制端没有设备的文件系统)。
    /// 引用先过纯函数解析(逃逸一律拒绝),再按<b>登记在案的行</b>找文件——磁盘上有但没有登记的文件读不到;行的作用域看不见(别的账户的会话)读不到。
    /// </summary>
    public AttachmentChunk Read(string url, long offset, int length)
    {
        var locator = Core.Domain.Attachments.ParseUrl(url) ?? throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.invalidLocation", "Invalid attachment reference.");
        if (locator.Scope != Core.Domain.Attachments.Session || locator.OwnerId is not { } owner || !repository.OwnerVisible(owner)
            || repository.FindByOwnerAndFileName(owner, locator.FileName) is not { } row || !IsManaged(row.Path) || !File.Exists(row.Path))
        {
            throw new AttachmentException(AttachmentFailure.NotFound, "attachments.error.notFound", "The attachment does not exist.");
        }

        using var stream = new FileStream(row.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset < 0 || offset > stream.Length)
        {
            throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.offset", "The offset is outside the file.");
        }

        stream.Position = offset;
        var buffer = new byte[Math.Clamp(length, 1, MaxChunkBytes)];
        var read = stream.Read(buffer, 0, buffer.Length);
        return new AttachmentChunk(buffer.AsSpan(0, read).ToArray(), stream.Length, Core.Domain.Attachments.MimeOfExt(row.Path), stream.Position >= stream.Length);
    }

    private AttachmentInfo ToInfo(AttachmentRow row)
    {
        var locator = new AttachmentLocator(row.Scope, row.OwnerId, Path.GetFileName(row.Path));
        return new AttachmentInfo(
            row.Id, row.Scope, row.OwnerId, row.DisplayName ?? Path.GetFileName(row.Path), Core.Domain.Attachments.MimeOfExt(row.Path), row.Size, row.Checksum, row.CreatedAt,
            Core.Domain.Attachments.BuildUrl(locator) ?? throw new AttachmentException(AttachmentFailure.Invalid, "attachments.error.invalidLocation", "Invalid attachment record."));
    }

    // ───────────────────────── 复制 / 分支时的图片搬家 ─────────────────────────

    /// <summary>
    /// 复制 / 分支出新会话之后,把消息里引用<b>源会话</b>托管图片的 <c>ncw://</c> 地址搬成新会话自己的:拷一份文件、登记成新消息的已提交附件、改写内容块里的地址。
    /// 为什么要搬:删掉源会话会连它的附件一起删,新会话的图不能跟着没了(也不能共用一个物理文件,那要引用计数)。
    /// 源文件不在了就保持原引用(转录里一个失效地址,和源会话里一样,不比原来更糟);外部来源的引用原样保留。返回搬了几张。
    /// </summary>
    public int Relocate(string sourceSessionId, string newSessionId)
    {
        var moved = 0;
        foreach (var message in sessions.History(newSessionId))
        {
            var refs = AttachmentRepository.RefsOf(message.PartsJson).Where(r => r.OwnerId == sourceSessionId).ToList();
            if (refs.Count == 0)
            {
                continue;
            }

            var parts = message.PartsJson;
            foreach (var locator in refs)
            {
                if (repository.FindByOwnerAndFileName(sourceSessionId, locator.FileName) is not { } row || !IsManaged(row.Path) || !File.Exists(row.Path))
                {
                    continue;
                }

                var target = Path.Combine(Root, "sessions", newSessionId, Path.GetFileName(row.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(row.Path, target, overwrite: true);
                var id = $"{message.Id}:moved:{Ulid.New(clock)}";
                repository.PutDraft(row with { Id = id, OwnerId = newSessionId, Path = target, CreatedAt = Now });
                var from = Core.Domain.Attachments.BuildUrl(locator)!;
                var to = Core.Domain.Attachments.BuildUrl(locator with { OwnerId = newSessionId })!;
                parts = parts.Replace(from, to, StringComparison.Ordinal);
                moved++;
            }

            if (parts != message.PartsJson)
            {
                sessions.ReplacePartsIfUnchanged(newSessionId, message.Id, message.PartsJson, parts);
                repository.CommitByRefs(AttachmentRepository.RefsOf(parts).Where(r => r.OwnerId == newSessionId).ToList(), message.Id, newSessionId);
            }
        }

        return moved;
    }

    /// <summary>删会话之后回收那些不再被任何行引用的受管文件(调用方在删之前用 <see cref="SessionRepository.AttachmentPaths"/> 记路径)。返回没删掉的路径。</summary>
    public IReadOnlyList<string> RemoveUnreferencedFiles(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!IsManaged(path) || repository.ReferenceCount(path) > 0)
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(path);
            }
        }

        return failed;
    }

    /// <summary>路径是不是附件根下 <c>sessions/</c> 里的受管文件。</summary>
    public bool IsManaged(string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(Path.Combine(Root, "sessions")), Path.GetFullPath(path));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
