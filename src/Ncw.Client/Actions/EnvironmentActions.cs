using Ncw.Client.Devices;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 执行环境(SSH 多设备执行)的用户意图(规则 4:一个意图一个方法)。真源在设备的 <c>EnvironmentRegistry</c> 和各环境实例;状态变化经 <c>environments</c> 主题进入
/// <see cref="Stores.ClientStores.Environments"/>,这里<b>不直接改 Store</b>,返回值只用来报告失败和一次性结果。
/// ★ 凭据:密码、私钥口令、代理密码、私钥文件路径只在 <see cref="CreateAsync"/> / <see cref="UpdateAsync"/> / <see cref="TestAsync"/> 的请求里单向送出,
/// 回来的 DTO 里只有「有没有」;<see cref="AnswerCredentialAsync"/> 的回答同样只往设备送(「记住」由设备用 NCK1 存,控制端永远取不回)。
/// 配置的写入需要 owner,读取 / 连接 / 回答连接提问需要 member(<see cref="ActionOutcome.Forbidden"/> 报告角色不够)。
/// 失败的结构化原因在 <see cref="ActionResult{T}.ErrorKey"/>(<c>environments.*</c>),界面自己生成文字。
/// </summary>
public sealed class EnvironmentActions(DeviceRegistry devices, IConversationCommands conversation) : IEnvironmentCommands
{
    public Task<ActionResult<EnvironmentsTopicState>> ListAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.List, Unit.Value, ct));

    /// <summary>新建 SSH 环境。设备铸 id;密码 / 口令存 NCK1,不回传。</summary>
    public Task<ActionResult<EnvironmentDto>> CreateAsync(string deviceId, EnvironmentInput profile, EnvironmentSecretsInput? secrets = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.Create, new EnvironmentCreateParams(profile, secrets), ct, idem: Idem("create")));

    /// <summary>改配置(整份以 <paramref name="profile"/> 为准;私钥 / 配置文件路径与凭据的 null = 保持,空串 = 清除)。<paramref name="expectedRevision"/> 对不上是 <see cref="ActionOutcome.Conflict"/>。</summary>
    public Task<ActionResult<EnvironmentDto>> UpdateAsync(
        string deviceId, string environmentId, long expectedRevision, EnvironmentInput profile, EnvironmentSecretsInput? secrets = null, bool confirmTargetChange = false, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.Update, new EnvironmentUpdateParams(environmentId, expectedRevision, profile, secrets, confirmTargetChange), ct, idem: Idem("update")));

    /// <summary>删除环境(连同它保存的凭据)。还有项目绑定在上面是 <see cref="ActionOutcome.Conflict"/>(<c>environments.inUse</c>)。</summary>
    public Task<ActionResult<Unit>> RemoveAsync(string deviceId, string environmentId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.Remove, new EnvironmentIdParams(environmentId), ct, idem: Idem("remove")));

    public Task<ActionResult<Unit>> SetDefaultAsync(string deviceId, string environmentId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.SetDefault, new EnvironmentIdParams(environmentId), ct, idem: Idem("setDefault")));

    /// <summary>
    /// 连接(或失败后重新连接),等这次尝试结束。连接过程中的主机指纹确认、凭据提问以交互的形式出现(<see cref="AnswerHostKeyAsync"/> / <see cref="AnswerCredentialAsync"/>),
    /// 任何控制端可答。失败是结果:<see cref="EnvironmentDto.State"/> = <c>failed</c> + <see cref="EnvironmentDto.Error"/>。取消 <paramref name="ct"/> 会取消设备上的连接尝试。
    /// </summary>
    public Task<ActionResult<EnvironmentDto>> ConnectAsync(string deviceId, string environmentId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(environmentId), ct, idem: Idem("connect")));

    /// <summary>断开并回收连接(配置保留)。有 Run / 终端 / 后台 Shell 在用是 <see cref="ActionOutcome.Conflict"/>(<c>environments.busy</c>)。</summary>
    public Task<ActionResult<EnvironmentDto>> DisconnectAsync(string deviceId, string environmentId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(environmentId), ct, idem: Idem("disconnect")));

    /// <summary>试连接(可以是还没保存的草稿)。失败是结果里的 key,不是错误。</summary>
    public Task<ActionResult<EnvironmentTestResult>> TestAsync(string deviceId, EnvironmentInput profile, string? existingId = null, EnvironmentSecretsInput? secrets = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.Test, new EnvironmentTestParams(profile, existingId, secrets), ct, idem: Idem("test")));

    /// <summary>浏览环境里的远程目录(新建项目选根目录)。<paramref name="path"/> 缺省 = 远程 home。</summary>
    public Task<ActionResult<RemoteDirectoryDto>> BrowseAsync(string deviceId, string environmentId, string? path = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.Browse, new EnvironmentBrowseParams(environmentId, path), ct));

    /// <summary>
    /// 在 SSH 环境里添加项目(<paramref name="remotePath"/> 是远程主机上已存在的目录)。设备会先连接环境(首次指纹 / 凭据以交互出现,所以可能等一会儿);
    /// 项目栏的变化经设备的 <c>projects.changed</c> 推送进 Store,这里只返回新项目(带环境 id),方便调用方选中它。
    /// </summary>
    public Task<ActionResult<ProjectDto>> AddProjectAsync(string deviceId, string environmentId, string remotePath, string? name = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(remotePath, name, environmentId), ct, idem: Idem("addProject")));

    /// <summary>读设备上的 <c>~/.ssh/config</c>,列出可导入的主机别名(不连接任何主机)。</summary>
    public Task<ActionResult<SshConfigImportResult>> ImportSshConfigAsync(string deviceId, string? configFile = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.ImportSshConfig, new ImportSshConfigParams(configFile), ct));

    /// <summary>已知主机(钉扎的指纹)。</summary>
    public Task<ActionResult<KnownHostsResult>> KnownHostsAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.KnownHosts, Unit.Value, ct));

    /// <summary>忘记一台主机的钉扎(用户明确选择「信任新指纹」的第一步;指纹变化的拒绝从不自动覆盖,只有这里能移除)。</summary>
    public Task<ActionResult<Unit>> ForgetHostKeyAsync(string deviceId, string hostName, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(EnvironmentsMethods.ForgetHostKey, new ForgetHostKeyParams(hostName), ct, idem: Idem("forgetHostKey")));

    /// <summary>回答主机指纹确认:<paramref name="trust"/> 为真 = 信任并钉扎,继续连接;为假 = 拒绝(不发任何凭据)。先到先得:别的控制端抢先了得到 <see cref="InteractionOutcome.AlreadyHandled"/>。</summary>
    public Task<InteractionResult> AnswerHostKeyAsync(string deviceId, SshHostKeyInteractionDto interaction, bool trust, CancellationToken ct = default) =>
        conversation.RespondAsync(deviceId, interaction, new SshHostKeyResponseDto(trust), ct);

    /// <summary>
    /// 回答凭据提问:<paramref name="answers"/> 为 null = 取消(连接以 <c>cancelled</c> 结束);密码 / 口令一项,键盘交互式每个问题一项。
    /// <paramref name="remember"/> 只对 <see cref="SshCredentialInteractionDto.CanRemember"/> 的提问有效。回答只往设备送,不进任何 Store。
    /// </summary>
    public Task<InteractionResult> AnswerCredentialAsync(string deviceId, SshCredentialInteractionDto interaction, IReadOnlyList<string>? answers, bool remember = false, CancellationToken ct = default) =>
        conversation.RespondAsync(deviceId, interaction, new SshCredentialResponseDto(answers is null ? null : [.. answers], remember), ct);

    private static string Idem(string what) => $"environments.{what}:{Guid.NewGuid():N}";

    private async Task<ActionResult<T>> Run<T>(string deviceId, Func<DeviceLink, Task<T>> call)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<T>.NotConnected();
        }

        try
        {
            return ActionResult<T>.Success(await call(link).ConfigureAwait(false));
        }
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}

/// <summary>执行环境的意图(<see cref="EnvironmentActions"/> 的全部方法)。ViewModel 依赖这个接口,单测里换成假实现。</summary>
public interface IEnvironmentCommands
{
    Task<ActionResult<EnvironmentsTopicState>> ListAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<EnvironmentDto>> CreateAsync(string deviceId, EnvironmentInput profile, EnvironmentSecretsInput? secrets = null, CancellationToken ct = default);

    Task<ActionResult<EnvironmentDto>> UpdateAsync(
        string deviceId, string environmentId, long expectedRevision, EnvironmentInput profile, EnvironmentSecretsInput? secrets = null, bool confirmTargetChange = false, CancellationToken ct = default);

    Task<ActionResult<Unit>> RemoveAsync(string deviceId, string environmentId, CancellationToken ct = default);

    Task<ActionResult<Unit>> SetDefaultAsync(string deviceId, string environmentId, CancellationToken ct = default);

    Task<ActionResult<EnvironmentDto>> ConnectAsync(string deviceId, string environmentId, CancellationToken ct = default);

    Task<ActionResult<EnvironmentDto>> DisconnectAsync(string deviceId, string environmentId, CancellationToken ct = default);

    Task<ActionResult<EnvironmentTestResult>> TestAsync(string deviceId, EnvironmentInput profile, string? existingId = null, EnvironmentSecretsInput? secrets = null, CancellationToken ct = default);

    Task<ActionResult<RemoteDirectoryDto>> BrowseAsync(string deviceId, string environmentId, string? path = null, CancellationToken ct = default);

    Task<ActionResult<ProjectDto>> AddProjectAsync(string deviceId, string environmentId, string remotePath, string? name = null, CancellationToken ct = default);

    Task<ActionResult<SshConfigImportResult>> ImportSshConfigAsync(string deviceId, string? configFile = null, CancellationToken ct = default);

    Task<ActionResult<KnownHostsResult>> KnownHostsAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<Unit>> ForgetHostKeyAsync(string deviceId, string hostName, CancellationToken ct = default);

    Task<InteractionResult> AnswerHostKeyAsync(string deviceId, SshHostKeyInteractionDto interaction, bool trust, CancellationToken ct = default);

    Task<InteractionResult> AnswerCredentialAsync(string deviceId, SshCredentialInteractionDto interaction, IReadOnlyList<string>? answers, bool remember = false, CancellationToken ct = default);
}
