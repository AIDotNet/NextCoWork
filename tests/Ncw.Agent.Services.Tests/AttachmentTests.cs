using System.Security.Cryptography;
using Ncw.Agent.Services.Attachments;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Services.Tests;

/// <summary>
/// 附件服务。移植自 2.x 的 <c>attachment.test.ts</c>(上传落盘 / 去重 / MIME)、<c>attachment-commit.test.ts</c>(提交)、<c>attachment-cleanup.test.ts</c>(移除只删受管文件)、<c>session-clone.test.ts</c> 的图片搬家,
/// 并补上分块上传的顺序 / 幂等 / 过期 / 上限。真实临时目录 + 真 SQLite + 假时钟。
/// </summary>
public sealed class AttachmentTests : IDisposable
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82];

    private readonly TempDatabase _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero));
    private readonly AttachmentRepository _repo;
    private readonly SessionRepository _sessions;
    private readonly AttachmentService _service;

    public AttachmentTests()
    {
        _repo = new AttachmentRepository(_db.Db, _clock);
        _sessions = new SessionRepository(_db.Db, _clock, _repo);
        _service = new AttachmentService(_db.Root, _clock, _repo, _sessions);
        _db.AddWorkspace("ws");
    }

    public void Dispose() => _db.Dispose();

    private AttachmentInfo Upload(string session, string name, string mime, byte[] bytes, int chunk = 7)
    {
        var id = _service.BeginUpload(session, name, mime, bytes.Length);
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            Assert.Equal(Math.Min(bytes.Length, offset + chunk), _service.WriteChunk(id, offset, bytes[offset..Math.Min(bytes.Length, offset + chunk)]));
        }

        return _service.Commit(id);
    }

    // ───────────────────────── 上传 ─────────────────────────

    [Fact]
    public void A_chunked_upload_lands_atomically_under_a_ulid_name_and_is_registered_as_a_draft_with_its_display_name()
    {
        var a = Upload("s1", "截图 2026.png", "image/png", Png);

        Assert.Equal(("session", "s1", "截图 2026.png", "image/png", (long)Png.Length), (a.Scope, a.OwnerId, a.DisplayName, a.Mime, a.Size));
        Assert.StartsWith("ncw://attachments/sessions/s1/", a.Url);
        Assert.EndsWith(".png", a.Url);
        Assert.DoesNotContain("截图", a.Url); // 用户给的文件名绝不进路径
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Png)), a.Checksum);
        var onDisk = Directory.GetFiles(Path.Combine(_service.Root, "sessions", "s1"));
        Assert.Equal(Png, File.ReadAllBytes(Assert.Single(onDisk)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_service.Root, "sessions", "s1"), ".*.tmp")); // 没有残片
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_service.Root, ".uploads"))); // 暂存已清

        var listed = Assert.Single(_service.ListBySession("s1"));
        Assert.Equal((a.Id, "截图 2026.png"), (listed.Id, listed.DisplayName)); // 重启后显示名读回,不退化成 ULID
    }

    [Fact]
    public void Identical_bytes_in_the_same_session_dedupe_to_one_file_but_never_across_sessions()
    {
        var first = Upload("s1", "a.png", "image/png", Png);
        var again = Upload("s1", "b.png", "image/png", Png);
        var other = Upload("s2", "c.png", "image/png", Png);

        Assert.Equal(first.Id, again.Id);          // 同一个文件
        Assert.Equal("b.png", again.DisplayName);  // 但显示名是这次上传的
        Assert.NotEqual(first.Id, other.Id);       // 跨会话不共用:删一个会话会波及另一个
        Assert.Single(Directory.GetFiles(Path.Combine(_service.Root, "sessions", "s1")));
    }

    [Fact]
    public void A_deduped_hit_whose_file_is_gone_or_damaged_is_stored_again_instead_of_returning_a_dead_attachment()
    {
        var first = Upload("s1", "a.png", "image/png", Png);
        File.Delete(Directory.GetFiles(Path.Combine(_service.Root, "sessions", "s1")).Single());

        var again = Upload("s1", "a.png", "image/png", Png);

        Assert.NotEqual(first.Id, again.Id);
        Assert.Single(Directory.GetFiles(Path.Combine(_service.Root, "sessions", "s1")));
    }

    [Fact]
    public void Image_type_follows_the_bytes_not_the_declared_mime_and_aliases_are_accepted()
    {
        Assert.Equal("image/png", Upload("s1", "x.jpg", "image/jpeg", Png).Mime);                          // 声明是 JPEG,字节是 PNG
        Assert.EndsWith(".png", Upload("s2", "x.jpg", "image/jpeg", Png).Url);
        var jpeg = new byte[] { 255, 216, 255, 224, 0, 16, 74, 70, 73, 70 };
        Assert.Equal("image/jpeg", Upload("s3", "photo", "image/jpg", jpeg).Mime);                          // 别名 + 文件头
        Assert.Equal("image/png", Upload("s4", "pic.png", "application/octet-stream", Png).Mime);           // 没声明:用扩展名 / 文件头
        var notImage = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Equal("attachments.error.unsupportedImage", Assert.Throws<AttachmentException>(() => Upload("s5", "x.bmp", "image/bmp", notImage)).Key);   // 字节认不出、声明又不在白名单
        Assert.Equal("image/png", Upload("s6", "x.png", "image/png", notImage).Mime);                                                                           // 认不出字节时接受发送端白名单里的声明(2.x 同款)
    }

    [Fact]
    public void A_non_image_keeps_its_declared_type_and_unknown_types_get_a_bin_extension_not_none()
    {
        var pdf = Upload("s1", "doc.pdf", "application/pdf", [37, 80, 68, 70, 45]);
        Assert.Equal("application/pdf", pdf.Mime);
        Assert.EndsWith(".pdf", pdf.Url);
        var blob = Upload("s1", "data", "application/x-weird", [9, 9, 9, 9]);
        Assert.EndsWith(".bin", blob.Url);
    }

    [Fact]
    public void Empty_oversized_and_malformed_uploads_are_refused_with_stable_keys_and_parameters()
    {
        var empty = Assert.Throws<AttachmentException>(() => _service.BeginUpload("s1", "e.txt", "text/plain", 0));
        Assert.Equal(("attachments.error.empty", "e.txt"), (empty.Key, empty.Params["name"]));
        var big = Assert.Throws<AttachmentException>(() => _service.BeginUpload("s1", "big.bin", "application/zip", Core.Domain.Attachments.MaxBytes + 1));
        Assert.Equal((AttachmentFailure.TooLarge, "attachments.error.tooLarge", "32"), (big.Kind, big.Key, big.Params["limit"]));
        Assert.Equal("attachments.error.invalidLocation", Assert.Throws<AttachmentException>(() => _service.BeginUpload("../x", "a.txt", "text/plain", 5)).Key); // 会话 id 不能是路径
        Assert.Equal("attachments.error.invalidLocation", Assert.Throws<AttachmentException>(() => _service.BeginUpload("a/b", "a.txt", "text/plain", 5)).Key);
    }

    [Fact]
    public void Chunks_must_arrive_in_order_a_resent_chunk_is_idempotent_overflow_and_incomplete_commits_are_refused()
    {
        var id = _service.BeginUpload("s1", "a.txt", "text/plain", 10);
        Assert.Equal(4, _service.WriteChunk(id, 0, [1, 2, 3, 4]));
        Assert.Equal(4, _service.WriteChunk(id, 0, [1, 2, 3, 4]));    // 重发已收到的块:幂等
        Assert.Equal("attachments.error.chunkOrder", Assert.Throws<AttachmentException>(() => _service.WriteChunk(id, 6, [5])).Key);
        Assert.Equal("attachments.error.incomplete", Assert.Throws<AttachmentException>(() => _service.Commit(id)).Key);   // 还没收完:不落盘
        Assert.Equal(10, _service.WriteChunk(id, 4, [5, 6, 7, 8, 9, 10]));                                                  // 上传还在,继续发完
        Assert.Equal(10, _service.Commit(id).Size);
        var id2 = _service.BeginUpload("s1", "a.txt", "text/plain", 3);
        Assert.Equal("attachments.error.chunkOverflow", Assert.Throws<AttachmentException>(() => _service.WriteChunk(id2, 0, [1, 2, 3, 4])).Key);
        Assert.Equal("attachments.error.chunkTooLarge", Assert.Throws<AttachmentException>(() => _service.WriteChunk(id2, 0, new byte[AttachmentService.MaxChunkBytes + 1])).Key);
    }

    [Fact]
    public void An_abandoned_upload_expires_and_the_number_of_open_uploads_is_capped()
    {
        var id = _service.BeginUpload("s1", "a.txt", "text/plain", 10);
        _clock.Advance(AttachmentService.UploadLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal("attachments.error.uploadNotFound", Assert.Throws<AttachmentException>(() => _service.WriteChunk(id, 0, [1])).Key);
        Assert.Empty(Directory.GetFiles(Path.Combine(_service.Root, ".uploads")));

        for (var i = 0; i < AttachmentService.MaxOpenUploads; i++)
        {
            _service.BeginUpload("s1", $"f{i}.txt", "text/plain", 5);
        }

        Assert.Equal("attachments.error.tooManyUploads", Assert.Throws<AttachmentException>(() => _service.BeginUpload("s1", "one-more.txt", "text/plain", 5)).Key);
    }

    [Fact]
    public void Aborting_an_upload_deletes_its_staging_file()
    {
        var id = _service.BeginUpload("s1", "a.txt", "text/plain", 10);
        _service.WriteChunk(id, 0, [1, 2]);
        _service.AbortUpload(id);
        _service.AbortUpload(id); // 幂等
        Assert.Empty(Directory.GetFiles(Path.Combine(_service.Root, ".uploads")));
    }

    // ───────────────────────── 移除 / 读取 ─────────────────────────

    [Fact]
    public void Removing_a_draft_deletes_the_managed_file_and_the_row_and_removing_it_again_is_a_no_op()
    {
        var a = Upload("s1", "a.png", "image/png", Png);
        var file = Directory.GetFiles(Path.Combine(_service.Root, "sessions", "s1")).Single();

        _service.Remove(a.Id);

        Assert.False(File.Exists(file));
        Assert.Empty(_service.ListBySession("s1"));
        _service.Remove(a.Id); // 幂等
        _service.Remove("no-such-id");
    }

    [Fact]
    public void A_committed_attachment_cannot_be_removed_through_its_id_and_an_external_path_row_never_touches_the_users_file()
    {
        _sessions.Create(new SessionCreate("ws", "s1"));
        var a = Upload("s1", "a.png", "image/png", Png);
        _sessions.Commit("s1", new MessageRecord("m1", "user", $"[{{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"{a.Url}\"}}]", 1));
        Assert.Equal(("attachments.error.committed", AttachmentFailure.Conflict), (Assert.Throws<AttachmentException>(() => _service.Remove(a.Id)).Key, Assert.Throws<AttachmentException>(() => _service.Remove(a.Id)).Kind));

        // 外部绝对路径(Agent 生成的图只登记引用):删行不删文件
        var outside = Path.Combine(Path.GetTempPath(), $"ncwext-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(outside, Png);
        try
        {
            _repo.PutDraft(new AttachmentRow("ext", "session", "s9", null, null, outside, Png.Length, "x", "draft", "ext.png", 1));
            _service.Remove("ext");
            Assert.True(File.Exists(outside));
            Assert.Null(_repo.Get("ext"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Reading_by_reference_returns_chunks_only_for_registered_files_and_rejects_escapes()
    {
        var a = Upload("s1", "a.png", "image/png", Png);

        var first = _service.Read(a.Url, 0, 10);
        Assert.Equal(Png[..10], first.Data);
        Assert.Equal(((long)Png.Length, "image/png", false), (first.Total, first.Mime, first.Eof));
        var rest = _service.Read(a.Url, 10, 1000);
        Assert.Equal(Png[10..], rest.Data);
        Assert.True(rest.Eof);

        Assert.Equal("attachments.error.invalidLocation", Assert.Throws<AttachmentException>(() => _service.Read("ncw://attachments/sessions/s1/%2e%2e%2f%2e%2e%2fsecret", 0, 10)).Key);
        Assert.Equal("attachments.error.notFound", Assert.Throws<AttachmentException>(() => _service.Read("ncw://attachments/sessions/s1/not-registered.png", 0, 10)).Key);
        // 磁盘上有、没有登记的文件读不到
        File.WriteAllBytes(Path.Combine(_service.Root, "sessions", "s1", "stray.png"), Png);
        Assert.Equal("attachments.error.notFound", Assert.Throws<AttachmentException>(() => _service.Read("ncw://attachments/sessions/s1/stray.png", 0, 10)).Key);
        Assert.Equal("attachments.error.notFound", Assert.Throws<AttachmentException>(() => _service.Read("ncw://attachments/themes/w.png", 0, 10)).Key); // theme 是无主 scope,不能经会话附件读取
    }

    // ───────────────────────── 复制 / 分支 / 删会话 ─────────────────────────

    private (SessionRecord Session, AttachmentInfo Image) SessionWithImage()
    {
        var session = _sessions.Create(new SessionCreate("ws", "src"));
        var image = Upload("src", "pic.png", "image/png", Png);
        _sessions.Commit("src", new MessageRecord("m1", "user", $"[{{\"type\":\"text\",\"text\":\"look\"}},{{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"{image.Url}\"}}]", 1));
        _sessions.Commit("src", new MessageRecord("m2", "assistant", "[{\"type\":\"text\",\"text\":\"nice\"}]", 2));
        return (session, image);
    }

    [Fact]
    public void Duplicating_moves_the_images_to_the_new_session_so_deleting_the_source_does_not_take_them_away()
    {
        var (_, image) = SessionWithImage();

        var copy = _sessions.Duplicate("src", "copy");
        Assert.Equal(1, _service.Relocate("src", copy.Id));

        var message = _sessions.History(copy.Id)[0];
        Assert.DoesNotContain("/sessions/src/", message.PartsJson);
        Assert.Contains($"/sessions/{copy.Id}/", message.PartsJson);
        var row = Assert.Single(_repo.ForMessage(message.Id));
        Assert.Equal(("committed", copy.Id), (row.Status, row.SessionId));

        var sourcePaths = _sessions.AttachmentPaths("src");
        _sessions.Delete("src");
        _service.RemoveUnreferencedFiles(sourcePaths);

        Assert.False(File.Exists(Path.Combine(_service.Root, "sessions", "src", Path.GetFileName(row.Path))));
        Assert.Equal(Png, File.ReadAllBytes(row.Path));                       // 副本还在
        Assert.Equal(Png.Length, _service.Read(Core.Domain.Attachments.BuildUrl(new Core.Domain.AttachmentLocator("session", copy.Id, Path.GetFileName(row.Path)))!, 0, 100).Data.Length);
        _ = image;
    }

    [Fact]
    public void Branching_moves_only_the_images_in_the_copied_prefix_and_a_missing_source_file_leaves_the_reference_as_is()
    {
        var (_, image) = SessionWithImage();
        File.Delete(Directory.GetFiles(Path.Combine(_service.Root, "sessions", "src")).Single());

        var branch = _sessions.Branch("src", "m1", "branch");
        Assert.Equal(0, _service.Relocate("src", branch.Id)); // 源文件已经不在:保持原引用,不比源会话里更糟

        Assert.Contains(image.Url, _sessions.History(branch.Id)[0].PartsJson);
    }

    [Fact]
    public void Deleting_a_session_reclaims_only_files_no_other_row_still_references()
    {
        var (_, image) = SessionWithImage();
        var copy = _sessions.Duplicate("src", "copy");
        _service.Relocate("src", copy.Id);
        var paths = _sessions.AttachmentPaths(copy.Id);

        _sessions.Delete(copy.Id);
        Assert.Empty(_service.RemoveUnreferencedFiles(paths));

        Assert.Single(Directory.GetFiles(Path.Combine(_service.Root, "sessions", "src"))); // 源会话的文件不受影响
        Assert.False(Directory.Exists(Path.Combine(_service.Root, "sessions", copy.Id)) && Directory.GetFiles(Path.Combine(_service.Root, "sessions", copy.Id)).Length > 0);
        _ = image;
    }
}
