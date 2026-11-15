using Ncw.Client.Devices;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// MCP 相关的用户意图(规则 4:一个意图一个方法)。真源在设备的 <c>McpService</c>;状态变化经 <c>mcp</c> 主题进入 <see cref="Stores.ClientStores"/> 里的 MCP 镜像,
/// 这里不直接改 Store。★ 密钥:<see cref="SetSecretsAsync"/> 只把明文送出去(整张 map 一次写完),回来的只有键名和「是否已设置」;
/// 明文没有取回的方法。写入类方法需要 owner(会在设备上起进程 / 连网),读取需要 member。
/// 失败的结构化原因在 <see cref="ActionResult{T}.ErrorKey"/>(<c>mcp.*</c>),界面自己生成文字。
/// </summary>
public sealed class McpActions(DeviceRegistry devices) : IMcpCommands
{
    /// <summary>主动拉一份全量状态(平时靠 <c>mcp</c> 主题;主题订阅前 / 对账时用)。</summary>
    public Task<ActionResult<McpTopicState>> ListAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.List, Unit.Value, ct));

    /// <summary>新增或修改一台服务器。返回第一次连接尝试结束后的状态(最长约 30 秒;失败时设备在后台退避重连)。</summary>
    public Task<ActionResult<McpServerStatusDto>> UpsertAsync(string deviceId, McpServerConfigDto config, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.Upsert, new McpUpsertParams(config), ct, idem: Idem("upsert")));

    /// <summary>删服务器(连同它的密钥)。幂等。</summary>
    public Task<ActionResult<McpRemoveResult>> RemoveAsync(string deviceId, string serverId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.Remove, new McpIdParams(serverId), ct, idem: Idem("remove")));

    /// <summary>启用 / 停用(停用只断开,不删配置)。</summary>
    public Task<ActionResult<McpServerStatusDto>> SetEnabledAsync(string deviceId, string serverId, bool enabled, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.SetEnabled, new McpSetEnabledParams(serverId, enabled), ct, idem: Idem("setEnabled")));

    /// <summary>手动重连(退避用尽之后、或想重来)。</summary>
    public Task<ActionResult<McpServerStatusDto>> ReconnectAsync(string deviceId, string serverId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.Reconnect, new McpIdParams(serverId), ct, idem: Idem("reconnect")));

    /// <summary>「测试连接」:真连一次、列工具、断开,不改设备状态。<paramref name="draft"/> 非 null = 测还没保存的配置(密钥仍取同 id 已存的那份)。失败是结果里的 key,不是错误。</summary>
    public Task<ActionResult<McpTestResultDto>> TestConnectionAsync(string deviceId, string serverId, McpServerConfigDto? draft = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.TestConnection, new McpTestParams(serverId, draft), ct, idem: Idem("testConnection")));

    /// <summary>写密钥(键名 → 明文值,整张 map 替换;配置没声明的键丢弃)。回来的只有键名和「是否已设置」。</summary>
    public Task<ActionResult<McpSecretsInfoDto>> SetSecretsAsync(string deviceId, string serverId, IReadOnlyDictionary<string, string> values, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.SetSecrets, new McpSetSecretsParams(serverId, new Dictionary<string, string>(values)), ct, idem: Idem("setSecrets")));

    /// <summary>这台服务器的密钥现在存了哪几个键(键名和是否已设置,绝不含值)。</summary>
    public Task<ActionResult<McpSecretsInfoDto>> GetSecretsInfoAsync(string deviceId, string serverId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.GetSecretsInfo, new McpIdParams(serverId), ct));

    /// <summary>服务器列出的资源(需要已连接)。</summary>
    public Task<ActionResult<McpResourcesResult>> ListResourcesAsync(string deviceId, string serverId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.ResourcesList, new McpIdParams(serverId), ct));

    /// <summary>服务器列出的提示(需要已连接)。</summary>
    public Task<ActionResult<McpPromptsResult>> ListPromptsAsync(string deviceId, string serverId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(McpMethods.PromptsList, new McpIdParams(serverId), ct));

    private static string Idem(string what) => $"mcp.{what}:{Guid.NewGuid():N}";

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
