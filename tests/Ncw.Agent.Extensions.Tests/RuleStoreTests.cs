using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>
/// 规则存放处(移植自 2.x <c>kernel/__tests__/local-settings.test.ts</c> 的权限部分和 <c>permission-rule.test.ts</c> 里的 <c>normalizeLocalSettings</c>)。
/// 项目作用域跑在真文件 + <c>LocalEnvironment</c> 上;「远程」用把环境包成 Ssh 种类的方式模拟。hooks 段随 Hooks 扩展,这里只验证它被原样保留。
/// </summary>
public sealed class RuleStoreTests : IDisposable
{
    private readonly Fixture _fx = new();
    private readonly ProjectRuleFile _store = new();

    public void Dispose() => _fx.Dispose();

    private RuleStoreContext Ctx(IExecutionEnvironment? env = null, bool withRoot = true) =>
        new("s1", env ?? _fx.Env, withRoot ? EnvPath.Parse(_fx.Root, Fixture.Style) : null);

    private JsonDocument Written() => JsonDocument.Parse(_fx.ReadSettings()!);

    // ── 读 ──

    [Fact]
    public async Task A_missing_file_means_no_rules_not_an_error()
    {
        Assert.Equal(RuleBuckets.Empty.Allow, (await _store.ReadAsync(Ctx(), default)).Allow);
        Assert.Empty((await _store.ReadAsync(Ctx(), default)).Deny);
    }

    [Fact]
    public async Task Without_a_workspace_nothing_is_read_and_a_write_says_it_cannot_be_stored()
    {
        _fx.PutSettings("""{"permissions":{"allow":["Bash"]}}""");
        Assert.Empty((await _store.ReadAsync(Ctx(withRoot: false), default)).Allow);
        var result = await _store.AddAsync(Ctx(withRoot: false), RuleBucket.Allow, "Bash", default);
        Assert.Equal(new RuleWriteResult(false, false, RuleWriteFailure.NoWorkspace), result);
    }

    [Fact]
    public async Task A_corrupt_json_file_reads_as_no_rules_it_must_not_block_a_run()
    {
        _fx.PutSettings("{ not json");
        Assert.Empty((await _store.ReadAsync(Ctx(), default)).Allow);
    }

    [Fact]
    public async Task Unreadable_rules_are_dropped_and_duplicates_collapsed_and_the_three_buckets_survive()
    {
        _fx.PutSettings("""{"version":1,"permissions":{"allow":["Bash(git status:*)","Bash(git status:*)","Ba sh",7,""],"deny":"nope"}}""");
        var buckets = await _store.ReadAsync(Ctx(), default);
        Assert.Equal(["Bash(git status:*)"], buckets.Allow);
        Assert.Empty(buckets.Deny);
        Assert.Empty(buckets.Ask);
    }

    [Fact]
    public async Task Any_shape_of_file_yields_usable_settings()
    {
        foreach (var raw in new[] { "null", "42", "\"x\"", "[]", "{}", """{"permissions":[]}""", """{"permissions":{"allow":"x"}}""" })
        {
            _fx.PutSettings(raw);
            var b = await _store.ReadAsync(Ctx(), default);
            Assert.Empty(b.Allow);
            Assert.Empty(b.Ask);
            Assert.Empty(b.Deny);
        }
    }

    [Fact]
    public async Task A_bucket_is_capped_at_500_rules_on_read()
    {
        var rules = string.Join(',', Enumerable.Range(0, 600).Select(i => $"\"Bash(c{i}:*)\""));
        _fx.PutSettings($$$"""{"permissions":{"allow":[{{{rules}}}]}}""");
        Assert.Equal(500, (await _store.ReadAsync(Ctx(), default)).Allow.Count);
    }

    [Fact]
    public async Task Comments_and_trailing_commas_hand_edited_are_tolerated()
    {
        _fx.PutSettings("{ // mine\n \"permissions\": { \"allow\": [\"Bash\",], }, }");
        Assert.Equal(["Bash"], (await _store.ReadAsync(Ctx(), default)).Allow);
    }

    [Fact]
    public async Task A_local_edit_takes_effect_on_the_next_read_the_cache_is_keyed_by_mtime_and_size()
    {
        _fx.PutSettings("""{"permissions":{"allow":["Bash"]}}""");
        Assert.Equal(["Bash"], (await _store.ReadAsync(Ctx(), default)).Allow);
        await Task.Delay(30);
        _fx.PutSettings("""{"permissions":{"allow":["Read","Grep"]}}""");
        Assert.Equal(["Read", "Grep"], (await _store.ReadAsync(Ctx(), default)).Allow);
    }

    // ── 写 ──

    [Fact]
    public async Task Writing_creates_the_directory_writes_the_rule_and_the_next_read_sees_it_immediately()
    {
        var result = await _store.AddAsync(Ctx(), RuleBucket.Allow, "Bash(git status:*)", default);
        Assert.Equal(RuleWriteResult.Written, result);
        using var doc = Written();
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(["Bash(git status:*)"], doc.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0, doc.RootElement.GetProperty("permissions").GetProperty("ask").GetArrayLength());
        Assert.Equal(["Bash(git status:*)"], (await _store.ReadAsync(Ctx(), default)).Allow);
    }

    [Fact]
    public async Task A_duplicate_rule_is_not_written_twice()
    {
        await _store.AddAsync(Ctx(), RuleBucket.Allow, "Bash", default);
        Assert.Equal(RuleWriteResult.AlreadyPresent, await _store.AddAsync(Ctx(), RuleBucket.Allow, "Bash", default));
        using var doc = Written();
        Assert.Single(doc.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray());
    }

    [Fact]
    public async Task Unknown_keys_are_preserved_what_a_newer_version_wrote_an_older_one_must_not_erase()
    {
        _fx.PutSettings("""{"version":2,"futureSection":{"a":1},"hooks":{"Stop":[{"id":"a","command":"x"}]},"permissions":{"allow":["Read"],"futureBucket":["x"]}}""");
        await _store.AddAsync(Ctx(), RuleBucket.Allow, "Bash", default);
        using var doc = Written();
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("futureSection").GetProperty("a").GetInt32());
        Assert.Equal("x", root.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("command").GetString());
        Assert.Equal("x", root.GetProperty("permissions").GetProperty("futureBucket")[0].GetString());
        Assert.Equal(["Read", "Bash"], root.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task An_unreadable_file_is_refused_overwriting_the_user_only_clicked_a_button_not_clear_my_config()
    {
        _fx.PutSettings("{ not json");
        Assert.Equal(new RuleWriteResult(false, false, RuleWriteFailure.Unreadable), await _store.AddAsync(Ctx(), RuleBucket.Allow, "Bash", default));
        Assert.Equal("{ not json", _fx.ReadSettings());
    }

    [Fact]
    public async Task A_full_bucket_says_so_instead_of_dropping_silently()
    {
        var rules = string.Join(',', Enumerable.Range(0, 500).Select(i => $"\"Bash(c{i}:*)\""));
        _fx.PutSettings($$$"""{"permissions":{"allow":[{{{rules}}}]}}""");
        Assert.Equal(new RuleWriteResult(false, false, RuleWriteFailure.Full), await _store.AddAsync(Ctx(), RuleBucket.Allow, "Bash(new:*)", default));
        Assert.Equal(RuleWriteResult.Written, await _store.AddAsync(Ctx(), RuleBucket.Deny, "Bash(new:*)", default)); // 别的桶不受影响
    }

    [Fact]
    public async Task Concurrent_writes_are_queued_and_the_later_one_sees_the_earlier()
    {
        await Task.WhenAll(
            _store.AddAsync(Ctx(), RuleBucket.Allow, "Bash(a:*)", default).AsTask(),
            _store.AddAsync(Ctx(), RuleBucket.Allow, "Bash(b:*)", default).AsTask(),
            _store.AddAsync(Ctx(), RuleBucket.Deny, "Bash(c:*)", default).AsTask());
        using var doc = Written();
        Assert.Equal(["Bash(a:*)", "Bash(b:*)"], doc.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal(["Bash(c:*)"], doc.RootElement.GetProperty("permissions").GetProperty("deny").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Many_parallel_writes_lose_nothing()
    {
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => _store.AddAsync(Ctx(), RuleBucket.Allow, $"Bash(r{i}:*)", default).AsTask()));
        Assert.Equal(40, (await _store.ReadAsync(Ctx(), default)).Allow.Count);
    }

    // ── 远程:失败即关闭 ──

    [Fact]
    public async Task A_remote_environment_rereads_every_time_no_cache()
    {
        var reads = 0;
        var env = new EnvironmentOverride(EnvironmentKind.Ssh, fs => new CountingFs(fs, () => reads++)).Wrap(_fx.Env);
        _fx.PutSettings("""{"permissions":{"allow":["Bash"]}}""");
        for (var i = 0; i < 3; i++)
        {
            await _store.ReadAsync(Ctx(env), default);
        }

        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task A_remote_environment_fails_closed_on_a_malformed_file_and_on_unreadable_files()
    {
        var env = new EnvironmentOverride(EnvironmentKind.Ssh).Wrap(_fx.Env);
        _fx.PutSettings("{ broken");
        await Assert.ThrowsAnyAsync<JsonException>(async () => await _store.ReadAsync(Ctx(env), default));

        var denied = new EnvironmentOverride(EnvironmentKind.Ssh, fs => new ThrowingStatFs(fs, new EnvironmentException(EnvironmentErrorCode.PermissionDenied))).Wrap(_fx.Env);
        await Assert.ThrowsAsync<EnvironmentException>(async () => await _store.ReadAsync(Ctx(denied), default));

        // 但「文件不存在」是正常状态
        var missing = new EnvironmentOverride(EnvironmentKind.Ssh, fs => new ThrowingStatFs(fs, new EnvironmentException(EnvironmentErrorCode.NotFound))).Wrap(_fx.Env);
        Assert.Empty((await _store.ReadAsync(Ctx(missing), default)).Deny);
    }

    [Fact]
    public async Task Remote_and_local_do_not_share_a_cache_key()
    {
        _fx.PutSettings("""{"permissions":{"allow":["Bash"]}}""");
        await _store.ReadAsync(Ctx(), default);
        var reads = 0;
        var env = new EnvironmentOverride(EnvironmentKind.Ssh, fs => new CountingFs(fs, () => reads++)).Wrap(_fx.Env);
        await _store.ReadAsync(Ctx(env), default);
        Assert.Equal(1, reads);
    }

    // ── 内存存放处与规则簿 ──

    [Fact]
    public async Task The_session_store_is_per_session_and_forget_drops_a_session()
    {
        var store = MemoryRuleStore.PerSession();
        await store.AddAsync(Ctx(), RuleBucket.Allow, "Bash(ls:*)", default);
        Assert.Equal(["Bash(ls:*)"], (await store.ReadAsync(Ctx(), default)).Allow);
        Assert.Empty((await store.ReadAsync(new RuleStoreContext("other", _fx.Env, null), default)).Allow);
        store.Forget("s1");
        Assert.Empty((await store.ReadAsync(Ctx(), default)).Allow);
    }

    [Fact]
    public async Task A_memory_store_caps_each_bucket_and_deduplicates()
    {
        var store = MemoryRuleStore.Process();
        Assert.Equal(RuleWriteResult.Written, await store.AddAsync(Ctx(), RuleBucket.Allow, "Bash", default));
        Assert.Equal(RuleWriteResult.AlreadyPresent, await store.AddAsync(Ctx(), RuleBucket.Allow, "Bash", default));
        for (var i = 1; i < 500; i++)
        {
            await store.AddAsync(Ctx(), RuleBucket.Allow, $"Bash(r{i}:*)", default);
        }

        Assert.Equal(RuleWriteFailure.Full, (await store.AddAsync(Ctx(), RuleBucket.Allow, "Bash(overflow:*)", default)).Failure);
    }

    [Fact]
    public async Task The_rule_book_reads_the_union_of_all_three_scopes_and_writes_to_the_chosen_one()
    {
        var global = MemoryRuleStore.Process();
        var book = PermissionRuleBook.CreateDefault(global);
        var ctx = Ctx();
        await book.AddAsync(RememberScope.Session, ctx, RuleBucket.Allow, "Bash(a:*)", default);
        await book.AddAsync(RememberScope.Project, ctx, RuleBucket.Allow, "Bash(b:*)", default);
        await book.AddAsync(RememberScope.Global, ctx, RuleBucket.Allow, "Bash(c:*)", default);
        await book.AddAsync(RememberScope.Global, ctx, RuleBucket.Deny, "Bash(rm:*)", default);

        Assert.Equal(["Bash(a:*)", "Bash(b:*)", "Bash(c:*)"], (await book.ReadAsync(ctx, default)).Allow.Order());
        Assert.Equal(["Bash(rm:*)"], (await book.ReadAsync(ctx, default)).Deny);

        // 各写到自己的那一处
        Assert.Equal(["Bash(a:*)"], (await book.Session.ReadAsync(ctx, default)).Allow);
        Assert.Equal(["Bash(b:*)"], (await book.Project.ReadAsync(ctx, default)).Allow);
        Assert.Equal(["Bash(c:*)"], (await global.ReadAsync(ctx, default)).Allow);
        using var doc = Written();
        Assert.Equal(["Bash(b:*)"], doc.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()));

        // 另一个会话看不见会话作用域,看得见项目和永久
        var other = new RuleStoreContext("s2", _fx.Env, ctx.Root);
        Assert.Equal(["Bash(b:*)", "Bash(c:*)"], (await book.ReadAsync(other, default)).Allow.Order());
    }

    private sealed class CountingFs(IEnvironmentFs inner, Action onRead) : DelegatingFs(inner)
    {
        public override ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default)
        {
            onRead();
            return base.OpenReadAsync(path, ct);
        }
    }

    private sealed class ThrowingStatFs(IEnvironmentFs inner, Exception error) : DelegatingFs(inner)
    {
        public override ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default) => throw error;
    }

    private abstract class DelegatingFs(IEnvironmentFs inner) : IEnvironmentFs
    {
        public virtual ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default) => inner.StatAsync(path, ct);

        public virtual ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default) => inner.LStatAsync(path, ct);

        public virtual ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default) => inner.RealPathAsync(path, ct);

        public virtual ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default) => inner.ListAsync(path, ct);

        public virtual ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default) => inner.OpenReadAsync(path, ct);

        public virtual ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default) => inner.OpenWriteAsync(path, options, ct);

        public virtual ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default) => inner.MkdirAsync(path, recursive, ct);

        public virtual ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default) => inner.RenameAsync(source, destination, replace, ct);

        public virtual ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default) => inner.DeleteFileAsync(path, ct);

        public virtual ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default) => inner.DeleteDirectoryAsync(path, recursive, ct);

        public virtual ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default) => inner.CopyFileAsync(source, destination, maxBytes, ct);

        public virtual IFsWatcher Watch(EnvPath directory, FsWatchOptions options) => inner.Watch(directory, options);
    }
}
