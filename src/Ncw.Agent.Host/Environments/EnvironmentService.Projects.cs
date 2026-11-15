using Ncw.Agent.Environments;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Environments;

internal sealed partial class EnvironmentService
{
    // ───────────────────────── 项目 → 执行环境(IProjectEnvironments) ─────────────────────────

    /// <summary>
    /// 项目在哪个环境里(同步版,给已经确保过就绪的路径用)。本机项目 = 本机环境。SSH 项目:环境是 Ready 或正在重连(此时操作最多等 ReadyWait)才返回;
    /// 还没连上 / 正在连接 / 失败 / 被禁用 / 已被删除都是 <see cref="ProtocolException"/>(<c>unavailable</c>,key 说明原因)——不悄悄当成本机目录用。
    /// </summary>
    public IExecutionEnvironment Resolve(WorkspaceRecord project)
    {
        if (project.EnvironmentKind != "connection")
        {
            return _registry.Get(EnvironmentId.LocalName);
        }

        var id = project.ConnectionId ?? throw EnvironmentErrors.Coded(ErrorCodes.NotFound, "The project's environment does not exist.", EnvironmentErrorKeys.NotFound);
        var view = ViewOf(id);
        if (!view.Enabled)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Unavailable, "The project's environment is disabled.", EnvironmentErrorKeys.Disabled);
        }

        var environment = _registry.Get(id);
        Touch(id);
        switch (environment.State)
        {
            case EnvironmentState.Ready or EnvironmentState.Reconnecting:
                return environment;
            case EnvironmentState.Failed:
                throw EnvironmentErrors.Coded(ErrorCodes.Unavailable, "The project's environment is not connected.",
                    EnvironmentErrors.KeyOf(_lastErrors.GetValueOrDefault(id, EnvironmentErrorCode.Disconnected)));
            default:
                throw EnvironmentErrors.NotConnected();
        }
    }

    /// <summary>
    /// 确保项目的执行环境已连接,再返回它。<b>只有 Idle(从没连过 / 被空闲回收)才会惰性发起连接</b>;Failed 不会自动重连(那是用户主动的「重新连接」,
    /// 否则被拒绝的指纹会在每次文件刷新时反复弹出);Connecting / Reconnecting 等它出结果。<paramref name="mode"/> 见 <see cref="ConnectMode"/>:viewer 不发起连接,没人在场的调用不问人。
    /// </summary>
    public async ValueTask<IExecutionEnvironment> ResolveReadyAsync(WorkspaceRecord project, CancellationToken ct)
    {
        await EnsureProjectReadyAsync(project, IsNonInteractive ? ConnectMode.NonInteractive : ConnectMode.Interactive, ct).ConfigureAwait(false);
        return Resolve(project);
    }

    public async ValueTask EnsureProjectReadyAsync(WorkspaceRecord project, ConnectMode mode, CancellationToken ct)
    {
        if (project.EnvironmentKind != "connection")
        {
            return;
        }

        var id = project.ConnectionId ?? throw EnvironmentErrors.Coded(ErrorCodes.NotFound, "The project's environment does not exist.", EnvironmentErrorKeys.NotFound);
        await EnsureReadyAsync(id, mode, ct).ConfigureAwait(false);
    }

    /// <summary>确保一个环境(按 id)已连接并返回它;规则见 <see cref="EnsureProjectReadyAsync"/>。</summary>
    public async ValueTask<IExecutionEnvironment> EnsureReadyAsync(string environmentId, ConnectMode mode, CancellationToken ct)
    {
        if (environmentId == EnvironmentId.LocalName)
        {
            return _registry.Get(environmentId);
        }

        var view = ViewOf(environmentId);
        if (!view.Enabled)
        {
            throw EnvironmentErrors.Coded(ErrorCodes.Unavailable, "The project's environment is disabled.", EnvironmentErrorKeys.Disabled);
        }

        var environment = _registry.Get(environmentId);
        Touch(environmentId);
        switch (environment.State)
        {
            case EnvironmentState.Ready:
                return environment;
            case EnvironmentState.Failed:
                throw EnvironmentErrors.Coded(ErrorCodes.Unavailable, "The project's environment is not connected.",
                    EnvironmentErrors.KeyOf(_lastErrors.GetValueOrDefault(environmentId, EnvironmentErrorCode.Disconnected)));
            case EnvironmentState.Idle when mode == ConnectMode.None:
                throw EnvironmentErrors.NotConnected();
        }

        try
        {
            // Idle:惰性连接(首次指纹 / 凭据以交互出现,等用户回答;非交互模式下要问人就失败);Connecting / Reconnecting:等进行中的那次出结果
            // 连接期间持有一个租约:连接可能比空闲阈值更久,而「刚连上、最后使用时间还是连接开始前的旧值」这个窗口里,
            // 空闲回收器会把一个刚变成 Ready 的环境又断掉。租约存在时 IsBusy 为真;释放时会 Touch,空闲计时从连接完成那一刻重新开始。
            using (Hold(environmentId))
            using (mode == ConnectMode.NonInteractive ? NonInteractiveScope() : null)
            {
                await environment.EnsureReadyAsync(ct).ConfigureAwait(false);
            }
        }
        catch (EnvironmentException e)
        {
            throw EnvironmentErrors.ToProtocol(e);
        }

        Touch(environmentId);
        return environment;
    }

    private ConnectionView ViewOf(string id)
    {
        lock (_lock)
        {
            return _views.GetValueOrDefault(id) ?? throw EnvironmentErrors.Coded(ErrorCodes.NotFound, "The project's environment does not exist.", EnvironmentErrorKeys.NotFound);
        }
    }

    /// <summary>环境里某个目录的规范路径(<c>projects.add</c> 绑定 SSH 项目用):先确保环境就绪;路径必须是绝对的、已存在的目录。返回(环境显示名, 远程规范路径)。</summary>
    public async Task<(string EnvironmentName, string RootPath)> ResolveRemoteDirectoryAsync(string environmentId, string path, ConnectMode mode, CancellationToken ct)
    {
        var view = ViewOf(environmentId);
        var environment = await EnsureReadyAsync(environmentId, mode, ct).ConfigureAwait(false);
        try
        {
            var requested = EnvPath.Absolute(path, environment.Facts.PathStyle);
            var real = await environment.Fs.RealPathAsync(requested, ct).ConfigureAwait(false);
            if (!(await environment.Fs.StatAsync(real, ct).ConfigureAwait(false)).IsDirectory)
            {
                throw EnvironmentErrors.Coded(ErrorCodes.InvalidRequest, "The path is not a directory.", EnvironmentErrorKeys.InvalidPath);
            }

            return (view.Name, real.Value);
        }
        catch (EnvironmentException e)
        {
            throw EnvironmentErrors.ToProtocol(e);
        }
    }
}
