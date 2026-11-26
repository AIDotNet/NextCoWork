using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Tests.Management;

/// <summary>脚本化的 <see cref="IEnvironmentCommands"/>(同时被 <c>Ncw.UI.Tests</c> 链接共用,模拟真实设备推送 <c>environments</c> 主题)。</summary>
public sealed class FakeEnvironments(ClientStores stores, string device = ManagementRig.Device) : IEnvironmentCommands
{
    private long _rev;

    public string DefaultId { get; set; } = "local";

    public List<EnvironmentDto> Environments { get; } = [LocalDto()];

    public List<SshConfigCandidateDto> ImportCandidates { get; } = [];

    public List<string> ImportWarnings { get; } = [];

    public List<KnownHostDto> KnownHosts { get; } = [];

    public List<(EnvironmentInput Profile, EnvironmentSecretsInput? Secrets)> Created { get; } = [];

    public List<(string Id, long Revision, EnvironmentInput Profile, EnvironmentSecretsInput? Secrets, bool Confirm)> Updated { get; } = [];

    public List<string> Removed { get; } = [];

    public List<string> Connected { get; } = [];

    public List<string> Disconnected { get; } = [];

    public List<string> ForgottenHosts { get; } = [];

    public List<(string Id, bool Trust)> HostKeyAnswers { get; } = [];

    public List<(string Id, IReadOnlyList<string>? Answers, bool Remember)> CredentialAnswers { get; } = [];

    public List<(string EnvId, string Path, string? Name)> AddedProjects { get; } = [];

    public Func<string, string?, RemoteDirectoryDto>? BrowseHandler { get; set; }

    public ActionResult<EnvironmentDto>? NextUpdateFailure { get; set; }

    public ActionResult<Unit>? NextRemoveFailure { get; set; }

    public ActionResult<RemoteDirectoryDto>? NextBrowseFailure { get; set; }

    public EnvironmentTestResult NextTestResult { get; set; } = new(true, null, 24, new EnvironmentFactsDto("linux", "x64", "dev-box", "ops"));

    public InteractionResult NextInteractionResult { get; set; } = new(InteractionOutcome.Answered);

    public static EnvironmentDto LocalDto(bool isDefault = true) =>
        new("local", "Local", EnvironmentKindNames.Local, EnvironmentStateNames.Ready, 1, isDefault, true, 1);

    public static EnvironmentDto SshDto(
        string id,
        string name,
        string state = EnvironmentStateNames.Idle,
        string host = "10.0.0.5",
        int port = 22,
        string user = "ops",
        bool enabled = true,
        bool isDefault = false,
        long revision = 1,
        EnvironmentErrorDto? error = null,
        long? latencyMs = null,
        bool hasPassword = false,
        bool hasKey = false,
        string kind = "manual",
        string? proxyJump = null,
        SshProxyDto? proxy = null) =>
        new(
            id,
            name,
            EnvironmentKindNames.Ssh,
            state,
            1,
            isDefault,
            enabled,
            revision,
            error,
            latencyMs,
            kind == "config"
                ? new SshTargetDto("config", host, null, null, hasKey, null, false)
                : new SshTargetDto("manual", host, port, user, hasKey, proxyJump, false),
            "auto",
            "auto",
            proxy,
            hasPassword,
            state == EnvironmentStateNames.Ready ? new EnvironmentFactsDto("linux", "x64", host, user) : null);

    public void Push()
    {
        var list = Environments.Select(e => e with { IsDefault = e.Id == DefaultId }).ToArray();
        var state = new EnvironmentsTopicState(++_rev, DefaultId, list);
        stores.Environments.Update(m => EnvironmentsReducer.ApplyTopic(m, device, state));
    }

    public Task<ActionResult<EnvironmentsTopicState>> ListAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new EnvironmentsTopicState(_rev, DefaultId, [.. Environments.Select(e => e with { IsDefault = e.Id == DefaultId })])));

    public Task<ActionResult<EnvironmentDto>> CreateAsync(string deviceId, EnvironmentInput profile, EnvironmentSecretsInput? secrets = null, CancellationToken ct = default)
    {
        Created.Add((profile, secrets));
        var id = $"env-{Created.Count}";
        var dto = new EnvironmentDto(
            id,
            profile.Name,
            EnvironmentKindNames.Ssh,
            EnvironmentStateNames.Idle,
            0,
            false,
            profile.Enabled,
            1,
            null,
            null,
            new SshTargetDto(profile.Target.Kind, profile.Target.Host, profile.Target.Port, profile.Target.Username, !string.IsNullOrEmpty(profile.Target.IdentityFile), profile.Target.ProxyJump, !string.IsNullOrEmpty(profile.Target.ConfigFile)),
            profile.AuthMethod,
            profile.Platform,
            profile.Proxy is { } p ? new SshProxyDto(p.Kind, p.Host, p.Port, p.Username, !string.IsNullOrEmpty(secrets?.ProxyPassword)) : null,
            !string.IsNullOrEmpty(secrets?.Password),
            null);
        Environments.Add(dto);
        Push();
        return Task.FromResult(R.Ok(dto));
    }

    public Task<ActionResult<EnvironmentDto>> UpdateAsync(
        string deviceId, string environmentId, long expectedRevision, EnvironmentInput profile, EnvironmentSecretsInput? secrets = null, bool confirmTargetChange = false, CancellationToken ct = default)
    {
        Updated.Add((environmentId, expectedRevision, profile, secrets, confirmTargetChange));
        if (NextUpdateFailure is { } fail)
        {
            NextUpdateFailure = null;
            return Task.FromResult(fail);
        }

        var idx = Environments.FindIndex(e => e.Id == environmentId);
        if (idx < 0)
        {
            return Task.FromResult(R.Fail<EnvironmentDto>(ActionOutcome.NotFound, EnvironmentErrorKeys.NotFound));
        }

        var old = Environments[idx];
        var next = old with
        {
            Name = profile.Name,
            Enabled = profile.Enabled,
            Revision = old.Revision + 1,
            AuthMethod = profile.AuthMethod,
            Platform = profile.Platform,
            Target = new SshTargetDto(profile.Target.Kind, profile.Target.Host, profile.Target.Port, profile.Target.Username,
                profile.Target.IdentityFile is null ? old.Target?.HasIdentityFile ?? false : profile.Target.IdentityFile.Length > 0,
                profile.Target.ProxyJump,
                profile.Target.ConfigFile is null ? old.Target?.HasConfigFile ?? false : profile.Target.ConfigFile.Length > 0),
            HasSavedPassword = secrets?.Password is null ? old.HasSavedPassword : secrets.Password.Length > 0,
        };
        Environments[idx] = next;
        Push();
        return Task.FromResult(R.Ok(next));
    }

    public Task<ActionResult<Unit>> RemoveAsync(string deviceId, string environmentId, CancellationToken ct = default)
    {
        if (NextRemoveFailure is { } fail)
        {
            NextRemoveFailure = null;
            return Task.FromResult(fail);
        }

        Removed.Add(environmentId);
        Environments.RemoveAll(e => e.Id == environmentId);
        if (DefaultId == environmentId)
        {
            DefaultId = "local";
        }

        Push();
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<ActionResult<Unit>> SetDefaultAsync(string deviceId, string environmentId, CancellationToken ct = default)
    {
        DefaultId = environmentId;
        Push();
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<ActionResult<EnvironmentDto>> ConnectAsync(string deviceId, string environmentId, CancellationToken ct = default)
    {
        Connected.Add(environmentId);
        var idx = Environments.FindIndex(e => e.Id == environmentId);
        if (idx < 0)
        {
            return Task.FromResult(R.Fail<EnvironmentDto>(ActionOutcome.NotFound, EnvironmentErrorKeys.NotFound));
        }

        var next = Environments[idx] with { State = EnvironmentStateNames.Ready, LatencyMs = 18, Error = null };
        Environments[idx] = next;
        Push();
        return Task.FromResult(R.Ok(next));
    }

    public Task<ActionResult<EnvironmentDto>> DisconnectAsync(string deviceId, string environmentId, CancellationToken ct = default)
    {
        Disconnected.Add(environmentId);
        var idx = Environments.FindIndex(e => e.Id == environmentId);
        if (idx < 0)
        {
            return Task.FromResult(R.Fail<EnvironmentDto>(ActionOutcome.NotFound, EnvironmentErrorKeys.NotFound));
        }

        var next = Environments[idx] with { State = EnvironmentStateNames.Idle, LatencyMs = null };
        Environments[idx] = next;
        Push();
        return Task.FromResult(R.Ok(next));
    }

    public Task<ActionResult<EnvironmentTestResult>> TestAsync(string deviceId, EnvironmentInput profile, string? existingId = null, EnvironmentSecretsInput? secrets = null, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(NextTestResult));

    public Task<ActionResult<RemoteDirectoryDto>> BrowseAsync(string deviceId, string environmentId, string? path = null, CancellationToken ct = default)
    {
        if (NextBrowseFailure is { } fail)
        {
            NextBrowseFailure = null;
            return Task.FromResult(fail);
        }

        if (BrowseHandler is not null)
        {
            return Task.FromResult(R.Ok(BrowseHandler(environmentId, path)));
        }

        var resolved = string.IsNullOrWhiteSpace(path) ? "/home/ops" : path;
        return Task.FromResult(R.Ok(new RemoteDirectoryDto(
            resolved,
            resolved == "/" ? null : "/home",
            [new RemoteCrumbDto("/", "/"), new RemoteCrumbDto("home", "/home"), new RemoteCrumbDto("ops", "/home/ops")],
            ["/"],
            [new RemoteDirEntryDto(".git", $"{resolved}/.git"), new RemoteDirEntryDto("api", $"{resolved}/api")],
            false,
            new EnvironmentFactsDto("linux", "x64", "dev-box", "ops"))));
    }

    public Task<ActionResult<ProjectDto>> AddProjectAsync(string deviceId, string environmentId, string remotePath, string? name = null, CancellationToken ct = default)
    {
        AddedProjects.Add((environmentId, remotePath, name));
        var dto = new ProjectDto($"ssh-{AddedProjects.Count}", name ?? "api", remotePath, "ssh", Environments.FirstOrDefault(e => e.Id == environmentId)?.Name ?? environmentId, 1_700_000_000_000, environmentId);
        return Task.FromResult(R.Ok(dto));
    }

    public Task<ActionResult<SshConfigImportResult>> ImportSshConfigAsync(string deviceId, string? configFile = null, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new SshConfigImportResult([.. ImportCandidates], [.. ImportWarnings])));

    public Task<ActionResult<KnownHostsResult>> KnownHostsAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new KnownHostsResult([.. KnownHosts])));

    public Task<ActionResult<Unit>> ForgetHostKeyAsync(string deviceId, string hostName, CancellationToken ct = default)
    {
        ForgottenHosts.Add(hostName);
        KnownHosts.RemoveAll(h => h.HostName == hostName);
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<InteractionResult> AnswerHostKeyAsync(string deviceId, SshHostKeyInteractionDto interaction, bool trust, CancellationToken ct = default)
    {
        HostKeyAnswers.Add((interaction.Id, trust));
        return Task.FromResult(NextInteractionResult);
    }

    public Task<InteractionResult> AnswerCredentialAsync(string deviceId, SshCredentialInteractionDto interaction, IReadOnlyList<string>? answers, bool remember = false, CancellationToken ct = default)
    {
        CredentialAnswers.Add((interaction.Id, answers, remember));
        return Task.FromResult(NextInteractionResult);
    }
}
