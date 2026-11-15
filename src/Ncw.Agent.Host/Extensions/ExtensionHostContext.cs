using Ncw.Agent.Environments;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Extensions;

/// <summary>一个项目在设备上的落点:登记记录、它的执行环境、根目录(执行环境里的绝对路径)。</summary>
internal sealed record ProjectTarget(WorkspaceRecord Project, IExecutionEnvironment Environment, EnvPath Root);

/// <summary>
/// 扩展域(Skills / 命令 / Hooks / MCP)在 Host 里装配时需要的设备事实。由 <c>ConversationService</c> 造一份(设备数据就绪之后),各领域的 <c>*Host.Create(context)</c> 从这里取东西,
/// 不各自去摸数据库、数据根和项目登记。<see cref="Project"/> 对不存在的项目抛 <c>not_found</c>、环境暂时不可用抛可重试的 <c>unavailable</c>(协议错误,路由原样下发)。
/// </summary>
internal sealed class ExtensionHostContext
{
    public required AgentDatabase Database { get; init; }

    public required KvStore Kv { get; init; }

    /// <summary>NCK1 凭据库(MCP 密钥等):明文只在 Agent 里,永远不进任何 DTO / 事件。</summary>
    public required CredentialStore Credentials { get; init; }

    public required string DataRoot { get; init; }

    public required string DeviceId { get; init; }

    public required TimeProvider Clock { get; init; }

    public required ILoopDiagnostics Log { get; init; }

    public required AgentServices Services { get; init; }

    /// <summary>设备本机环境:全局目录(<c>&lt;数据根&gt;/skills</c>、<c>commands</c>、<c>hooks.json</c>……)和「在设备上运行」的 MCP 服务器经它。</summary>
    public required IExecutionEnvironment LocalEnvironment { get; init; }

    public required Func<string, ProjectTarget> Project { get; init; }

    /// <summary>
    /// 确保项目的执行环境已连接(没人在场的后台路径用:MCP 服务器启动时连项目环境)。<b>非交互</b>:已钉扎的指纹 + 已保存的凭据能连上就连,要问人就失败(<c>unavailable</c>)。
    /// 项目不存在 / 本机项目 / 没有接线(测试)都是直接返回。
    /// </summary>
    public Func<string, CancellationToken, ValueTask>? EnsureProjectReady { get; init; }

    /// <summary>主题中心:有运行期状态的领域(MCP 的连接状态)在这里发布全量状态(主题的注册在 <c>AgentHost</c>,由装配方接上)。</summary>
    public required TopicHub Topics { get; init; }

    /// <summary>数据根下的某个全局目录 / 文件(本机环境里的路径)。</summary>
    public EnvPath GlobalPath(params string[] parts) =>
        EnvPath.Parse(DataRoot, LocalEnvironment.Facts.PathStyle).Join(parts);
}

/// <summary>
/// 扩展域的业务失败 → 协议错误:粗粒度的错误码给路由 / 通用处理,稳定的 key + 参数放进 <c>details</c>(<see cref="ProviderErrorDto"/>,
/// 客户端的 <c>ActionResult</c> 已经会解它),控制端按 key 翻译。<b>参数里不得含任何凭据 / 密钥。</b>
/// </summary>
internal static class ExtensionErrors
{
    public static ProtocolException Coded(string code, string message, string key, IReadOnlyDictionary<string, string>? parameters = null, bool retryable = false) =>
        new(code, message, retryable,
            FrameCodec.EncodePayload(new ProviderErrorDto(key, parameters is null ? [] : new Dictionary<string, string>(parameters)), PayloadShapes.GetShape<ProviderErrorDto>()));
}
