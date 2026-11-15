using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Cloud;
using Ncw.Agent.Cloud.Tests.Support;
using Ncw.Agent.Environments;
using Ncw.Agent.Host.Cloud;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Agent.Workspace.Git;

namespace Ncw.Agent.Cloud.Tests;

/// <summary>KMS 包裹的主密钥(02 A6「密钥」、05 B8):磁盘上只有包裹后的密文,凭据格式 NCK1 不变,本机模式不受影响。</summary>
public sealed class KeyWrapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ncw-kms-{Guid.NewGuid():N}"[..20]);

    public KeyWrapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        CredentialCrypto.UseSource(_dir, null);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static CloudConfig Config(string dir) => CloudConfigLoader.Build(
        new CloudConfigFile(
            dir, dir + "-ws", null, "https://o.example.com", "wss://r.example.com",
            [new OrchestratorKeyFile("k", Convert.ToBase64String(new byte[32]))], null, null, null, new CloudKmsFile("dev-env", "acct/sandbox")), "tok", null);

    [Fact]
    public async Task The_master_key_is_generated_wrapped_and_only_the_wrapped_form_is_on_disk()
    {
        var kms = new FakeKms();

        using var source = await KmsMasterKeySource.OpenAsync(_dir, kms, "acct/sandbox", default);

        var key = source.Load();
        Assert.Equal(32, key.Length);
        Assert.False(File.Exists(Path.Combine(_dir, CredentialCrypto.MasterKeyFileName))); // 没有明文密钥文件
        var wrapped = File.ReadAllText(KmsMasterKeySource.PathFor(_dir));
        Assert.DoesNotContain(Convert.ToBase64String(key), wrapped);
        Assert.DoesNotContain(Convert.ToHexStringLower(key), wrapped);
        Assert.Contains("kms-key-1", wrapped);
        Assert.Equal(1, kms.Wraps);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KmsMasterKeySource.PathFor(_dir)));
        }
    }

    [Fact]
    public async Task Reopening_unwraps_the_same_key_from_the_kms_and_callers_get_copies_they_may_zero()
    {
        var kms = new FakeKms();
        byte[] original;
        using (var first = await KmsMasterKeySource.OpenAsync(_dir, kms, "ctx", default))
        {
            original = first.Load();
        }

        using var again = await KmsMasterKeySource.OpenAsync(_dir, kms, "ctx", default);

        Assert.Equal(original, again.Load());
        Assert.Equal(1, kms.Unwraps);
        CryptographicOperations.ZeroMemory(again.Load()); // CredentialStore 用完会清零它拿到的副本
        Assert.Equal(original, again.Load());
        Assert.Equal(1, kms.Unwraps); // 运行中不再访问 KMS
    }

    [Fact]
    public async Task A_wrong_encryption_context_cannot_unwrap_the_key_so_another_tenants_volume_is_useless()
    {
        var kms = new FakeKms();
        using (await KmsMasterKeySource.OpenAsync(_dir, kms, "acct-A/sandbox-1", default))
        {
        }

        await Assert.ThrowsAsync<KeyWrapException>(() => KmsMasterKeySource.OpenAsync(_dir, kms, "acct-B/sandbox-9", default));
    }

    [Fact]
    public async Task Startup_maps_kms_refusal_and_outage_to_structured_failures_with_distinct_exit_codes()
    {
        var kms = new FakeKms();
        var config = Config(_dir);
        using (await CloudBoot.OpenKeySourceAsync(config, kms, default))
        {
        }

        kms.Deny = true;
        var rejected = await Assert.ThrowsAsync<CloudStartupException>(() => CloudBoot.OpenKeySourceAsync(config, kms, default));
        Assert.Equal((CloudStartupCodes.KmsRejected, CloudExitCodes.Dependency), (rejected.Code, rejected.ExitCode));

        kms.Deny = false;
        kms.Down = true;
        var down = await Assert.ThrowsAsync<CloudStartupException>(() => CloudBoot.OpenKeySourceAsync(config, kms, default));
        Assert.Equal((CloudStartupCodes.KmsUnavailable, CloudExitCodes.Dependency), (down.Code, down.ExitCode));
    }

    [Fact]
    public async Task Credentials_keep_the_NCK1_format_and_decrypt_with_the_unwrapped_key()
    {
        var kms = new FakeKms();
        using var source = await KmsMasterKeySource.OpenAsync(_dir, kms, "ctx", default);
        CredentialCrypto.UseSource(_dir, source);

        using var db = AgentDatabase.Open(_dir);
        var store = new CredentialStore(db, _dir, NoLegacySecrets.Instance);
        store.Set("provider:test", "sk-cloud-secret");

        Assert.Equal("sk-cloud-secret", store.Get("provider:test"));
        var blob = (byte[])db.Connection.CreateCommand().ExecuteScalarOf("SELECT blob FROM credentials WHERE ref = 'provider:test'")!;
        Assert.True(CredentialCrypto.IsProgramEncrypted(blob));
        Assert.Equal("sk-cloud-secret", CredentialCrypto.Decrypt(source.Load(), blob)); // 密文格式不变:同一把 32 字节密钥、同一个 NCK1 布局
        Assert.False(File.Exists(Path.Combine(_dir, CredentialCrypto.MasterKeyFileName)));
    }

    [Fact]
    public async Task A_plaintext_key_from_imported_local_data_is_wrapped_and_the_plaintext_file_is_deleted()
    {
        byte[] plain;
        using (var db = AgentDatabase.Open(_dir))
        {
            new CredentialStore(db, _dir, NoLegacySecrets.Instance).Set("provider:old", "sk-from-desktop");
            plain = CredentialCrypto.LoadMasterKey(_dir);
        }

        var kms = new FakeKms();
        using var source = await KmsMasterKeySource.OpenAsync(_dir, kms, "ctx", default);
        CredentialCrypto.UseSource(_dir, source);

        Assert.Equal(plain, source.Load());
        Assert.False(File.Exists(Path.Combine(_dir, CredentialCrypto.MasterKeyFileName)));
        Assert.True(File.Exists(KmsMasterKeySource.PathFor(_dir)));
        using var reopened = AgentDatabase.Open(_dir);
        Assert.Equal("sk-from-desktop", new CredentialStore(reopened, _dir, NoLegacySecrets.Instance).Get("provider:old"));
    }

    [Fact]
    public async Task Encrypted_rows_with_no_key_in_any_form_refuse_to_start_instead_of_minting_a_new_key()
    {
        using (var db = AgentDatabase.Open(_dir))
        {
            new CredentialStore(db, _dir, NoLegacySecrets.Instance).Set("provider:x", "secret");
        }

        File.Delete(Path.Combine(_dir, CredentialCrypto.MasterKeyFileName));
        SqliteConnection.ClearAllPools();

        var error = await Assert.ThrowsAsync<CloudStartupException>(() => CloudBoot.OpenKeySourceAsync(Config(_dir), new FakeKms(), default));

        Assert.Equal((CloudStartupCodes.KeyMissing, CloudExitCodes.Identity), (error.Code, error.ExitCode));
        Assert.False(File.Exists(KmsMasterKeySource.PathFor(_dir)));
    }

    [Fact]
    public async Task Local_mode_is_untouched_the_source_registry_is_per_data_root_and_reversible()
    {
        var other = Path.Combine(Path.GetTempPath(), $"ncw-kms-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(other);
        try
        {
            using var source = await KmsMasterKeySource.OpenAsync(_dir, new FakeKms(), "ctx", default);
            CredentialCrypto.UseSource(_dir, source);

            var local = CredentialCrypto.LoadOrCreateMasterKey(other); // 别的数据根仍然用文件
            Assert.True(File.Exists(Path.Combine(other, CredentialCrypto.MasterKeyFileName)));
            Assert.Equal(local, CredentialCrypto.LoadMasterKey(other));
            Assert.NotEqual(local, source.Load());

            CredentialCrypto.UseSource(_dir, null);
            Assert.Throws<FileNotFoundException>(() => CredentialCrypto.LoadMasterKey(_dir));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void An_unknown_kms_provider_or_a_missing_dev_opt_in_is_a_startup_failure_not_a_silent_downgrade()
    {
        var config = Config(_dir);

        var noOptIn = Assert.Throws<CloudStartupException>(() => CloudBoot.CreateKeyWrap(config, _ => null));
        Assert.Equal((CloudStartupCodes.KmsUnavailable, "kms.provider"), (noOptIn.Code, noOptIn.Field));

        if (!CloudBoot.DevKmsCompiledIn)
        {
            // 发布构建:dev-env 根本没编进来,即使有人设置了 opt-in 环境变量也必须是启动失败,不能静默降级成没有保护的 KMS。
            var forced = Assert.Throws<CloudStartupException>(() => CloudBoot.CreateKeyWrap(config, n => n == CloudBoot.AllowDevKmsEnv ? "1" : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
            Assert.Equal((CloudStartupCodes.KmsUnavailable, "kms.provider"), (forced.Code, forced.Field));
            return;
        }

        var noKey = Assert.Throws<CloudStartupException>(() => CloudBoot.CreateKeyWrap(config, n => n == CloudBoot.AllowDevKmsEnv ? "1" : null));
        Assert.Equal(CloudBoot.DevKmsKeyEnv, noKey.Field);

        var wrap = CloudBoot.CreateKeyWrap(config, n => n == CloudBoot.AllowDevKmsEnv ? "1" : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.Equal("dev-env", wrap.KeyId);
    }
}

internal static class SqliteExtensions
{
    public static object? ExecuteScalarOf(this SqliteCommand command, string sql)
    {
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

/// <summary>Git 接力(02 A6):克隆 URL 的校验、凭据环境变量(不进命令行、按主机作用域)、克隆经项目环境的 git、失败不留半成品。</summary>
public sealed class GitRelayTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncw-git-{Guid.NewGuid():N}"[..20]);
    private readonly LocalEnvironment _env = new("dev-test");

    public GitRelayTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        await _env.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private EnvPath P(string name) => EnvPath.Parse(Path.Combine(_root, name), _env.Facts.PathStyle);

    private async Task<string> GitAsync(string dir, params string[] args)
    {
        var result = await _env.RunAsync(new CommandSpec { Program = "git", Args = args, WorkingDirectory = EnvPath.Parse(dir, _env.Facts.PathStyle) }, new RunOptions { Timeout = TimeSpan.FromSeconds(30) });
        Assert.True(result.Succeeded, result.Stderr.ToText());
        return result.Stdout.ToText().Trim();
    }

    /// <summary>一个有一次提交、一个 feature 分支的本地「远程仓库」。</summary>
    private async Task<string> MakeRemoteAsync()
    {
        var remote = Path.Combine(_root, "remote");
        Directory.CreateDirectory(remote);
        await GitAsync(remote, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(remote, "hello.txt"), "hello from the remote");
        await GitAsync(remote, "add", ".");
        await GitAsync(remote, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "first");
        await GitAsync(remote, "branch", "feature");
        return remote;
    }

    /// <summary>让 https://example.com/repo.git 实际指向本地仓库(只有这个测试的环境变量这样做;真实的 GIT_ALLOW_PROTOCOL 限制在测试里放宽到 file)。</summary>
    private static Dictionary<string, string> Redirect(string remote) => new()
    {
        ["GIT_ALLOW_PROTOCOL"] = "file:https",
        ["GIT_CONFIG_COUNT"] = "1",
        ["GIT_CONFIG_KEY_0"] = $"url.file://{remote}.insteadOf",
        ["GIT_CONFIG_VALUE_0"] = "https://example.com/repo.git",
    };

    [Theory]
    [InlineData("https://github.com/org/repo.git", true)]
    [InlineData("https://git.example.com:8443/a/b", true)]
    [InlineData("http://github.com/org/repo.git", false)]
    [InlineData("ssh://git@github.com/org/repo.git", false)]
    [InlineData("git@github.com:org/repo.git", false)]
    [InlineData("file:///etc", false)]
    [InlineData("ext::sh -c 'touch /tmp/x'", false)]
    [InlineData("--upload-pack=touch /tmp/x", false)]
    [InlineData("https://user:pass@github.com/org/repo.git", false)] // 凭据走 git.credentials,不进 URL
    [InlineData("https://github.com/org/repo.git#frag", false)]
    [InlineData("https://github.com/org/\nrepo.git", false)]
    [InlineData("", false)]
    public void Only_plain_https_clone_urls_without_credentials_are_accepted(string url, bool ok) =>
        Assert.Equal(ok, GitCloner.TryParseUrl(url, out _));

    [Fact]
    public void Credentials_become_url_scoped_environment_variables_and_never_appear_in_arguments()
    {
        var env = GitCredentialEnvironment.Build([new GitHttpCredential("github.com", "x-access-token", "ghp_SECRET"), new GitHttpCredential("gitlab.example.com:8443", "oauth2", "glpat_OTHER")]);

        Assert.Equal("2", env["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.https://github.com/.extraHeader", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("Authorization: Basic " + Convert.ToBase64String("x-access-token:ghp_SECRET"u8), env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("http.https://gitlab.example.com:8443/.extraHeader", env["GIT_CONFIG_KEY_1"]);
        Assert.Empty(GitCredentialEnvironment.Build([]));
    }

    [Fact]
    public async Task A_real_git_sends_the_header_only_to_the_matching_host()
    {
        var env = GitCredentialEnvironment.Build([new GitHttpCredential("github.com", "x-access-token", "ghp_SECRET")]);

        async Task<string> HeaderFor(string url)
        {
            var result = await _env.RunAsync(
                new CommandSpec { Program = "git", Args = ["config", "--get-urlmatch", "http.extraHeader", url], WorkingDirectory = P("."), Environment = env },
                new RunOptions { Timeout = TimeSpan.FromSeconds(30) });
            return result.Stdout.ToText().Trim();
        }

        Assert.StartsWith("Authorization: Basic ", await HeaderFor("https://github.com/org/repo.git"));
        Assert.Equal("", await HeaderFor("https://evil.example.com/org/repo.git")); // 别的主机拿不到令牌
        Assert.Equal("", await HeaderFor("https://github.com.evil.example/org/repo.git"));
    }

    [Fact]
    public async Task Cloning_creates_the_target_checks_out_the_branch_and_leaves_a_real_repository()
    {
        var remote = await MakeRemoteAsync();
        Assert.True(GitCloner.TryParseUrl("https://example.com/repo.git", out var url));

        await GitCloner.CloneAsync(_env, P("work/clone-a"), new GitCloneRequest(url, null, Redirect(remote)), TimeSpan.FromSeconds(60));
        await GitCloner.CloneAsync(_env, P("work/clone-b"), new GitCloneRequest(url, "feature", Redirect(remote)), TimeSpan.FromSeconds(60));

        Assert.Equal("hello from the remote", await File.ReadAllTextAsync(Path.Combine(_root, "work/clone-a/hello.txt")));
        Assert.Equal("main", await GitAsync(Path.Combine(_root, "work/clone-a"), "branch", "--show-current"));
        Assert.Equal("feature", await GitAsync(Path.Combine(_root, "work/clone-b"), "branch", "--show-current"));
    }

    [Fact]
    public async Task A_failed_clone_leaves_no_directory_behind_and_never_echoes_the_token()
    {
        Assert.True(GitCloner.TryParseUrl("https://127.0.0.1:1/nope.git", out var url));
        var env = GitCredentialEnvironment.Build([new GitHttpCredential("127.0.0.1:1", "x-access-token", "ghp_SECRET_TOKEN")]);

        var error = await Assert.ThrowsAsync<GitException>(() => GitCloner.CloneAsync(_env, P("work/fail"), new GitCloneRequest(url, null, env, ["ghp_SECRET_TOKEN"]), TimeSpan.FromSeconds(60)));

        Assert.Equal(GitErrorKind.Failed, error.Kind);
        Assert.DoesNotContain("ghp_SECRET_TOKEN", error.Message);
        Assert.DoesNotContain(Convert.ToBase64String("x-access-token:ghp_SECRET_TOKEN"u8), error.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "work/fail")));
    }

    [Fact]
    public async Task A_non_empty_target_a_flag_like_branch_and_a_relative_target_are_refused_before_git_runs()
    {
        Assert.True(GitCloner.TryParseUrl("https://example.com/repo.git", out var url));
        Directory.CreateDirectory(Path.Combine(_root, "busy"));
        await File.WriteAllTextAsync(Path.Combine(_root, "busy/keep.txt"), "mine");

        var busy = await Assert.ThrowsAsync<GitException>(() => GitCloner.CloneAsync(_env, P("busy"), new GitCloneRequest(url, null, null), TimeSpan.FromSeconds(5)));
        Assert.Equal(GitCloner.TargetNotEmpty, busy.Key);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_root, "busy/keep.txt"))); // 用户的文件原封不动

        var flag = await Assert.ThrowsAsync<GitException>(() => GitCloner.CloneAsync(_env, P("x"), new GitCloneRequest(url, "--upload-pack=evil", null), TimeSpan.FromSeconds(5)));
        Assert.Equal(GitKeys.InvalidBranch, flag.Key);

        var relative = await Assert.ThrowsAsync<GitException>(() => GitCloner.CloneAsync(_env, EnvPath.Parse("rel/path", _env.Facts.PathStyle), new GitCloneRequest(url, null, null), TimeSpan.FromSeconds(5)));
        Assert.Equal(GitCloner.TargetInvalid, relative.Key);
    }

    [Fact]
    public void The_host_key_drops_default_ports_and_keeps_custom_ones()
    {
        Assert.True(GitCloner.TryParseUrl("https://GitHub.com/a/b", out var plain));
        Assert.True(GitCloner.TryParseUrl("https://git.example.com:8443/a/b", out var custom));

        Assert.Equal("github.com", GitCloner.HostKey(plain));
        Assert.Equal("git.example.com:8443", GitCloner.HostKey(custom));
    }
}
