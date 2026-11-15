using System.Diagnostics;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Environments;

internal sealed partial class EnvironmentService
{
    private const int MaxBrowseEntries = 2000;

    // ───────────────────────── 配置:增改删、默认 ─────────────────────────

    public EnvironmentsTopicState List() => State();

    public Task<EnvironmentDto> CreateAsync(EnvironmentCreateParams p)
    {
        var v = ValidateOrThrow(p.Profile, p.Secrets);
        var id = Guid.NewGuid().ToString("D");
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        // 先存凭据再写配置:存不了就什么都不写(不是「连接存下了、密码悄悄丢了」,同 2.x 的 upsertConnection)
        ApplySecrets(id, v, previous: null, p.Secrets);
        var json = EnvironmentProfiles.ToJson(id, v, null, now, 1);
        _repository.Put(json);
        var view = ConnectionView.From(_repository.Get(id)!) ?? throw new InvalidOperationException("A stored connection could not be read back.");
        lock (_lock)
        {
            _views[id] = view;
        }

        _registry.AddSsh(id, view.Name, EnvironmentProfiles.ToProfile(view, ProxyPasswordOf(id)));
        Touch(id);
        return Task.FromResult(Dto(id));
    }

    public async Task<EnvironmentDto> UpdateAsync(EnvironmentUpdateParams p)
    {
        var previous = ViewOfOrNotFound(p.Id);
        if (previous.Revision != p.ExpectedRevision)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Conflict, "The environment was changed since you read it.", EnvironmentErrorKeys.Conflict);
        }

        var v = ValidateOrThrow(p.Profile, p.Secrets);
        if (IsBusy(p.Id))
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Conflict, "The environment is in use.", EnvironmentErrorKeys.Busy);
        }

        var targetChanged = previous.TargetKind != v.TargetKind || previous.Host != v.Host || previous.Port != v.Port || previous.Username != v.Username
            || previous.ProxyJump != v.ProxyJump || previous.Platform != v.Platform;
        if (targetChanged && !p.ConfirmTargetChange && ProjectsUsing(p.Id) > 0)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Conflict, "Changing the target would repoint existing projects.", EnvironmentErrorKeys.TargetChangeNeedsConfirm);
        }

        var stored = _repository.Get(p.Id) ?? throw EnvironmentErrors.NotFound(p.Id);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        ApplySecrets(p.Id, v, previous, p.Secrets);
        _repository.Put(EnvironmentProfiles.ToJson(p.Id, v, stored.Json, now, previous.Revision + 1));
        var view = ConnectionView.From(_repository.Get(p.Id)!) ?? throw new InvalidOperationException("A stored connection could not be read back.");
        lock (_lock)
        {
            _views[p.Id] = view;
        }

        _primaryHosts.TryRemove(p.Id, out _);
        ClearRuntime(p.Id);
        await _registry.UpdateSshAsync(p.Id, view.Name, EnvironmentProfiles.ToProfile(view, ProxyPasswordOf(p.Id))).ConfigureAwait(false);
        return Dto(p.Id);
    }

    public async Task RemoveAsync(string id)
    {
        if (id == EnvironmentId.LocalName)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.InvalidRequest, "The local environment cannot be removed.", EnvironmentErrorKeys.LocalImmutable);
        }

        _ = ViewOfOrNotFound(id);
        if (ProjectsUsing(id) > 0)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Conflict, "Projects are bound to this environment.", EnvironmentErrorKeys.InUse);
        }

        if (IsBusy(id))
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Conflict, "The environment is in use.", EnvironmentErrorKeys.Busy);
        }

        _secrets.RemoveAll(id);
        _repository.Remove(id);
        lock (_lock)
        {
            _views.Remove(id);
            _holds.Remove(id);
            _lastUsed.Remove(id);
        }

        _primaryHosts.TryRemove(id, out _);
        ClearRuntime(id);
        await _registry.RemoveAsync(id).ConfigureAwait(false);
    }

    public void SetDefault(string id)
    {
        try
        {
            _registry.SetDefault(id);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.EnvironmentNotFound)
        {
            throw EnvironmentErrors.NotFound(id);
        }
    }

    private void PersistDefault()
    {
        try
        {
            _kv.Set(DefaultKey, _registry.DefaultId);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or StorageException)
        {
            _log.Warn("Saving the default environment failed.", e);
        }
    }

    private ValidatedProfile ValidateOrThrow(EnvironmentInput input, EnvironmentSecretsInput? secrets)
    {
        try
        {
            EnvironmentProfiles.ValidateSecrets(secrets);
            return EnvironmentProfiles.Validate(input);
        }
        catch (EnvironmentException e)
        {
            throw EnvironmentErrors.ToProtocol(e);
        }
    }

    private ConnectionView ViewOfOrNotFound(string id)
    {
        lock (_lock)
        {
            return _views.GetValueOrDefault(id) ?? throw EnvironmentErrors.NotFound(id);
        }
    }

    private int ProjectsUsing(string id) => _workspaces.List().Count(w => w.ConnectionId == id);

    private string? ProxyPasswordOf(string id)
    {
        try
        {
            return _secrets.Get(ConnectionSecrets.ProxyPasswordRef(id));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>写凭据:null 不动、空串清除、非空写入。写不了(主密钥读不出来 / 库错误)整个请求失败,什么都不改。</summary>
    private void ApplySecrets(string id, ValidatedProfile v, ConnectionView? previous, EnvironmentSecretsInput? secrets)
    {
        if (secrets is null)
        {
            return;
        }

        try
        {
            Apply(ConnectionSecrets.PasswordRef(id), secrets.Password);
            if (secrets.KeyPassphrase is not null)
            {
                var identity = v.IdentityFile is { Length: > 0 } given ? given : v.IdentityFile is null ? previous?.IdentityFile : null;
                if (string.IsNullOrEmpty(identity))
                {
                    if (secrets.KeyPassphrase.Length > 0)
                    {
                        throw EnvironmentErrors.Coded(ErrorCodes.InvalidRequest, "A key passphrase needs a private key file.", EnvironmentErrorKeys.InvalidProfile);
                    }
                }
                else
                {
                    Apply(ConnectionSecrets.PassphraseRef(id, identity), secrets.KeyPassphrase);
                }
            }

            if (secrets.ProxyPassword is not null)
            {
                if (v.Proxy is null && secrets.ProxyPassword.Length > 0)
                {
                    throw EnvironmentErrors.Coded(ErrorCodes.InvalidRequest, "A proxy password needs a proxy.", EnvironmentErrorKeys.InvalidProfile);
                }

                Apply(ConnectionSecrets.ProxyPasswordRef(id), secrets.ProxyPassword);
            }
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or StorageException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Unavailable, "The credential store could not be written.", EnvironmentErrorKeys.SecretsUnavailable);
        }

        void Apply(string reference, string? value)
        {
            if (value is null)
            {
                return;
            }

            if (value.Length == 0)
            {
                _secrets.Remove(reference);
            }
            else
            {
                _secrets.Set(reference, value);
            }
        }
    }

    // ───────────────────────── 连接 ─────────────────────────

    /// <summary>
    /// 连接(或失败后重新连接),等这次尝试结束。失败是结果(<c>failed</c> + 错误 key),不是协议错误;请求被取消则连接尝试一并取消(环境回到 idle)。
    /// 这是用户主动的操作:<b>Failed 之后也会重试</b>(同 <see cref="IExecutionEnvironment.EnsureReadyAsync"/> 的约定)。
    /// </summary>
    public async Task<EnvironmentDto> ConnectAsync(string id, CancellationToken ct)
    {
        if (id == EnvironmentId.LocalName)
        {
            return Dto(id);
        }

        var view = ViewOfOrNotFound(id);
        if (!view.Enabled)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Unavailable, "The environment is disabled.", EnvironmentErrorKeys.Disabled);
        }

        var environment = _registry.Get(id);
        Touch(id);
        try
        {
            await environment.EnsureReadyAsync(ct).ConfigureAwait(false);
        }
        catch (EnvironmentException)
        {
            // 失败是结果:状态(failed + 错误 key)已经由环境实例的状态事件记下
        }

        Touch(id);
        return Dto(id);
    }

    public async Task<EnvironmentDto> DisconnectAsync(string id)
    {
        if (id == EnvironmentId.LocalName)
        {
            return Dto(id);
        }

        _ = ViewOfOrNotFound(id);
        if (IsBusy(id))
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Conflict, "The environment is in use.", EnvironmentErrorKeys.Busy);
        }

        if (await _registry.DisconnectAsync(id).ConfigureAwait(false))
        {
            ClearRuntime(id);
            Publish();
        }

        return Dto(id);
    }

    // ───────────────────────── 试连接 ─────────────────────────

    public async Task<EnvironmentTestResult> TestAsync(EnvironmentTestParams p, CancellationToken ct)
    {
        var v = ValidateOrThrow(p.Profile, p.Secrets);
        ConnectionView? existing = null;
        if (p.ExistingId is { Length: > 0 } existingId)
        {
            existing = ViewOfOrNotFound(existingId);
        }

        var tempId = $"test-{_ids.NewId()}";
        string? Path(string? input, string? stored) => input is null ? stored : input.Length == 0 ? null : input;
        var view = new ConnectionView(
            tempId, v.Name, true, 1, v.TargetKind, v.Host, v.Port, v.Username,
            Path(v.IdentityFile, existing?.IdentityFile), Path(v.ConfigFile, existing?.ConfigFile), v.ProxyJump, v.AuthMethod, v.Platform, v.Proxy);

        // 草稿里现填的凭据优先;没填就用要改的那个环境已保存的(它们只在这次试连接里用,不会记到临时 id 下)
        string? password = p.Secrets?.Password;
        string? passphrase = p.Secrets?.KeyPassphrase;
        string? proxyPassword = p.Secrets?.ProxyPassword;
        if (existing is not null)
        {
            password ??= SavedOrNull(ConnectionSecrets.PasswordRef(existing.Id));
            if (view.IdentityFile is { Length: > 0 } identity)
            {
                passphrase ??= SavedOrNull(ConnectionSecrets.PassphraseRef(existing.Id, identity));
            }

            proxyPassword ??= SavedOrNull(ConnectionSecrets.ProxyPasswordRef(existing.Id));
        }

        HostKeyMismatchDto? mismatch = null;
        var callbacks = new SshCallbacks(
            _broker, secrets: null, _deviceId, tempId, v.Name, () => null, m => mismatch = m, () => NonInteractiveFlag.Value, password, passphrase);
        var options = new SshEnvironmentOptions
        {
            Reconnect = ReconnectPolicy.Never,
            ReadyWait = _sshOptions.ReadyWait,
            KillGrace = _sshOptions.KillGrace,
            LocalUser = _sshOptions.LocalUser,
            Agent = _sshOptions.Agent,
            Delay = _sshOptions.Delay,
        };
        await using var environment = new SshEnvironment(new EnvironmentId(_deviceId, tempId), EnvironmentProfiles.ToProfile(view, proxyPassword), ServicesFor(callbacks), options);
        try
        {
            await environment.EnsureReadyAsync(ct).ConfigureAwait(false);
            var start = _clock.GetTimestamp();
            await environment.Fs.StatAsync(EnvPath.Parse(environment.Facts.Home, environment.Facts.PathStyle), ct).ConfigureAwait(false);
            return new EnvironmentTestResult(true, LatencyMs: (long)_clock.GetElapsedTime(start).TotalMilliseconds, Facts: FactsOf(environment));
        }
        catch (EnvironmentException e)
        {
            return new EnvironmentTestResult(false, new EnvironmentErrorDto(EnvironmentErrors.KeyOf(e.Code), e.Code == EnvironmentErrorCode.HostKeyChanged ? mismatch : null));
        }
    }

    private string? SavedOrNull(string reference)
    {
        try
        {
            return _secrets.Get(reference);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    // ───────────────────────── 浏览远程目录 ─────────────────────────

    public async Task<RemoteDirectoryDto> BrowseAsync(EnvironmentBrowseParams p, ConnectMode mode, CancellationToken ct)
    {
        if (p.EnvironmentId == EnvironmentId.LocalName)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.InvalidRequest, "Only SSH environments can be browsed.", EnvironmentErrorKeys.NotRemote);
        }

        var environment = await EnsureReadyAsync(p.EnvironmentId, mode, ct).ConfigureAwait(false);
        try
        {
            var style = environment.Facts.PathStyle;
            var start = p.Path is { Length: > 0 } ? EnvPath.Absolute(p.Path, style) : EnvPath.Parse(environment.Facts.Home, style);
            var directory = await environment.Fs.RealPathAsync(start, ct).ConfigureAwait(false);
            if (!(await environment.Fs.StatAsync(directory, ct).ConfigureAwait(false)).IsDirectory)
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotDirectory);
            }

            var listed = await environment.Fs.ListAsync(directory, ct).ConfigureAwait(false);
            var directories = new List<RemoteDirEntryDto>();
            var symlinkChecks = 0;
            foreach (var entry in listed)
            {
                var isDirectory = entry.Info.IsDirectory;
                if (entry.Info.IsSymlink && symlinkChecks++ < 200)
                {
                    // 指向目录的符号链接也是可以进去的目录
                    try
                    {
                        isDirectory = (await environment.Fs.StatAsync(directory.Join(entry.Name), ct).ConfigureAwait(false)).IsDirectory;
                    }
                    catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.PermissionDenied)
                    {
                    }
                }

                if (isDirectory)
                {
                    directories.Add(new RemoteDirEntryDto(entry.Name, directory.Join(entry.Name).Value));
                }
            }

            directories.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            var truncated = directories.Count > MaxBrowseEntries;
            var crumbs = new List<RemoteCrumbDto>();
            var cursor = directory;
            for (var depth = 0; depth < 128; depth++)
            {
                var parent = cursor.Parent;
                var atRoot = parent.Value == cursor.Value;
                crumbs.Insert(0, new RemoteCrumbDto(atRoot ? cursor.Value : cursor.FileName, cursor.Value));
                if (atRoot)
                {
                    break;
                }

                cursor = parent;
            }

            var parentPath = directory.Parent.Value == directory.Value ? null : directory.Parent.Value;
            return new RemoteDirectoryDto(
                directory.Value, parentPath, [.. crumbs], await RootsAsync(environment, directory, ct).ConfigureAwait(false),
                [.. directories.Take(MaxBrowseEntries)], truncated, FactsOf(environment)!);
        }
        catch (EnvironmentException e)
        {
            throw EnvironmentErrors.ToProtocol(e);
        }
    }

    /// <summary>可选的根:POSIX 只有 <c>/</c>;Windows 列各个盘符(经 PowerShell,2.x 同款)。拿不到时为空,界面只靠面包屑。</summary>
    private static async Task<string[]> RootsAsync(IExecutionEnvironment environment, EnvPath directory, CancellationToken ct)
    {
        if (environment.Facts.Os != EnvironmentOs.Windows)
        {
            return ["/"];
        }

        try
        {
            var result = await environment.RunAsync(
                CommandSpec.Shell("ConvertTo-Json -Compress -InputObject @([System.IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady } | ForEach-Object { $_.RootDirectory.FullName })") with { WorkingDirectory = directory },
                new RunOptions { Timeout = TimeSpan.FromSeconds(10) }, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return [];
            }

            using var json = JsonDocument.Parse(result.Stdout.ToText().Trim());
            var roots = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().Select(static e => e.GetString())
                : [json.RootElement.GetString()];
            return [.. roots.OfType<string>().Where(static r => r.Length is >= 2 and <= 4 && char.IsAsciiLetter(r[0]) && r[1] == ':').Take(26)];
        }
        catch (Exception e) when (e is JsonException or EnvironmentException or InvalidOperationException)
        {
            return [];
        }
    }

    // ───────────────────────── ~/.ssh/config、已知主机 ─────────────────────────

    public SshConfigImportResult ImportSshConfig(ImportSshConfigParams p)
    {
        var files = _options.ConfigFiles ?? new PhysicalSshConfigFiles();
        var config = SshConfig.Load(files, string.IsNullOrEmpty(p.ConfigFile) ? null : p.ConfigFile);
        var localUser = _sshOptions.LocalUser ?? Environment.UserName;
        var candidates = new List<SshConfigCandidateDto>();
        foreach (var alias in config.ListHosts())
        {
            var resolved = config.Resolve(alias, null, localUser);
            var warnings = resolved.Warnings.Select(static w => "environments.import." + w).ToList();
            // ProxyCommand 会执行任意命令作为传输:不支持(06 §8),导入时就说清楚
            var importable = resolved.ProxyCommand is null || resolved.ProxyJump is not null;
            if (!importable)
            {
                warnings.Add("environments.import.proxyCommand");
            }

            candidates.Add(new SshConfigCandidateDto(
                alias, resolved.HostName, resolved.Port, resolved.User, resolved.IdentityFiles.Count > 0, resolved.ProxyJump, importable, [.. warnings]));
        }

        return new SshConfigImportResult([.. candidates], []);
    }

    public async Task<KnownHostsResult> KnownHostsAsync(CancellationToken ct)
    {
        var list = await _hostKeys.ListAsync(ct).ConfigureAwait(false);
        return new KnownHostsResult([.. list.Select(static h => new KnownHostDto(h.HostName, h.Key.Algorithm, h.Key.FingerprintSha256))]);
    }

    public async Task ForgetHostKeyAsync(string hostName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostName) || hostName.Length > 300)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.InvalidRequest, "The host name is invalid.", EnvironmentErrorKeys.InvalidProfile);
        }

        await _hostKeys.RemoveAsync(hostName, ct).ConfigureAwait(false);
    }
}
