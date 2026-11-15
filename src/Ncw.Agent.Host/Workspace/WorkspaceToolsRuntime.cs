using Ncw.Agent.Environments;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Storage;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Workspace;

/// <summary>
/// 一个已解析的项目:登记记录 + 它的执行环境 + 环境里的根目录。工作区工具(files / git / review / terminals)都从这里起步,
/// 所以「项目 → 哪台设备的哪个环境的哪个目录」只有一处答案(AGENTS.md §5:涉及执行的东西必须带环境标识)。
/// </summary>
internal sealed record ProjectContext(WorkspaceRecord Project, IExecutionEnvironment Environment, EnvPath Root);

/// <summary>
/// 工作区工具(M4:files / git / review / terminals)在 Host 里的装配(设备数据就绪后创建一份,和对话链路同生命周期)。
/// 各领域的服务和处理器按分部类拆在 <c>Runtime.*.cs</c> 里,每个领域一个文件;这里只放公共的项目解析和生命周期。
/// 数据没就绪时(<see cref="AgentHost"/> 还没创建它)各领域的 <c>Register</c> 收到的访问器抛可重试的 <c>unavailable</c>。
/// </summary>
internal sealed partial class WorkspaceToolsRuntime : IAsyncDisposable
{
    private readonly ConversationService _conversation;

    public WorkspaceToolsRuntime(
        AgentDatabase database, ConversationService conversation, Providers.ProviderRuntime providers, Ncw.Protocol.Session.TopicHub topics,
        TimeProvider clock, AgentServices services, ILoopDiagnostics log)
    {
        _conversation = conversation;
        Database = database;
        Providers = providers;
        Topics = topics;
        Clock = clock;
        Services = services;
        Log = log;
        InitFiles();
        InitGit();
        InitReview();
        InitTerminals();
    }

    public AgentDatabase Database { get; }

    public Providers.ProviderRuntime Providers { get; }

    public Ncw.Protocol.Session.TopicHub Topics { get; }

    public TimeProvider Clock { get; }

    public AgentServices Services { get; }

    public ILoopDiagnostics Log { get; }

    public ConversationService Conversation => _conversation;

    /// <summary>项目 → (环境, 根目录)。项目不存在 <c>not_found</c>;环境暂时不可用(SSH 断开)是 <c>unavailable</c>(可重试)。</summary>
    public ProjectContext Project(string workspaceId)
    {
        var project = _conversation.RequireProject(workspaceId);
        var environment = _conversation.EnvironmentOf(project);
        return new ProjectContext(project, environment, EnvPath.Parse(project.RootPath, environment.Facts.PathStyle));
    }

    /// <summary>项目被移除(<c>projects.remove</c>):各领域丢掉它名下的状态(终端关掉、文件索引缓存丢弃)。</summary>
    public async ValueTask OnProjectRemovedAsync(string workspaceId)
    {
        await ProjectRemovedFilesAsync(workspaceId).ConfigureAwait(false);
        await ProjectRemovedTerminalsAsync(workspaceId).ConfigureAwait(false);
    }

    /// <summary>设备停止:终端全部终止并释放。必须在关库之前、环境释放之前。</summary>
    public async ValueTask DisposeAsync()
    {
        await DisposeTerminalsAsync().ConfigureAwait(false);
    }

    // 每个领域在自己的 Runtime.<领域>.cs 里实现(没有要做的事就留空)。
    partial void InitFiles();

    partial void InitGit();

    partial void InitReview();

    partial void InitTerminals();

    private partial ValueTask ProjectRemovedFilesAsync(string workspaceId);

    private partial ValueTask ProjectRemovedTerminalsAsync(string workspaceId);

    private partial ValueTask DisposeTerminalsAsync();
}
