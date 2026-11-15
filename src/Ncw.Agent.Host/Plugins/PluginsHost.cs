using System.Net;
using Ncw.Agent.Environments;
using Ncw.Agent.Host.Extensions;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Plugins;
using Ncw.Agent.Skills.Install;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Agent.Storage.Sessions;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Plugins;

/// <summary>
/// 插件宿主在 Host 里的装配:把插件系统的端口接到设备事实上——状态 / 配置 / 私有存储在库里的 kv 表(2.x 的键 <c>plugins.state</c> / <c>plugins.config.&lt;id&gt;</c> / <c>plugins.kv.&lt;id&gt;</c>,
/// 所以 2.x 数据被原地接管之后用户的启停与授权原样可用)、插件密钥在 NCK1 凭据库、包目录在 <c>&lt;数据根&gt;/plugins</c>(设备本机环境)、项目解析走项目登记、网络经注入的 HTTP。
/// 工具经 <see cref="ToolRegistry"/>(来源 <c>plugin:&lt;id&gt;</c>)进每回合快照;Skills 经 <c>ISkillPluginSource</c>;拦截器经 <c>PermissionExtension(interceptors: …)</c>;上下文经会话扩展。
/// </summary>
internal static class PluginsHost
{
    public static PluginManager Create(ExtensionHostContext context, ToolRegistry tools, Func<string, string?> workspaceOfSession)
    {
        var services = context.Services;
        return new PluginManager(new PluginManagerOptions
        {
            Environment = context.LocalEnvironment,
            Root = context.GlobalPath("plugins"),
            Kv = new KvPluginStore(context.Kv),
            Tools = tools,
            Secrets = new CredentialPluginSecrets(context.Credentials),
            Http = services.PluginHttp ?? DefaultPluginHttp.Instance,
            Workspaces = new ProjectPluginWorkspaces(context),
            // 市场:账户访问令牌取自 account.attachDelegatedToken 交付的委托账户(只在内存里,每次请求现取)。还没交付 / 已过期 = 没有令牌 → 市场安装返回 plugins.authRequired,浏览照常;交付之后立即恢复。
            Market = services.PluginMarket ?? new HttpPluginMarketClient(services.MarketHttpClient ?? MarketHttp.Instance, accessToken: () => services.ResolveAccount(context.Clock).AccessToken),
            Clock = context.Clock,
            Log = context.Log,
            Limits = services.PluginLimits ?? new PluginRuntimeLimits(),
            AppInfo = new PluginAppInfo("NextCoWork", AgentHost.Version, "en-US"),
            WorkspaceOfSession = workspaceOfSession,
        });
    }
}

/// <summary>kv 表的插件适配(2.x 的 key → json)。</summary>
internal sealed class KvPluginStore(KvStore kv) : IPluginKv
{
    public string? Get(string key) => kv.Get(key);

    public void Set(string key, string json) => kv.Set(key, json);
}

/// <summary>NCK1 凭据库的插件适配:明文只在 Agent 里,键由宿主加 <c>plugin:&lt;id&gt;:</c> 前缀。</summary>
internal sealed class CredentialPluginSecrets(CredentialStore credentials) : IPluginSecrets
{
    public string? Get(string reference) => credentials.Get(reference);

    public void Set(string reference, string value) => credentials.Set(reference, value);

    public bool Remove(string reference) => credentials.Remove(reference);
}

/// <summary>项目登记的插件适配。项目不存在 / 环境暂时不可用 = null(调用方当作「没有这个工作区」,不是崩溃)。</summary>
internal sealed class ProjectPluginWorkspaces(ExtensionHostContext context) : IPluginWorkspaces
{
    public PluginWorkspaceTarget? Resolve(string workspaceId)
    {
        try
        {
            var target = context.Project(workspaceId);
            return new PluginWorkspaceTarget(workspaceId, target.Project.Name, target.Environment, target.Root);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>net.fetch</c> 的真实出口:<b>一次只发一跳,不跟随重定向</b>(逐跳的 URL 门在插件宿主里),自动解压,有总超时;正文边读边数字节,到上限就停。
/// User-Agent 只写 <c>NextCoWork/3.x</c>,不带任何用户或设备标识;不用 cookie。
/// </summary>
internal sealed class DefaultPluginHttp : IPluginHttp
{
    public static DefaultPluginHttp Instance { get; } = new();

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public async Task<PluginHttpResponse> SendAsync(PluginHttpRequest request, int maxBodyBytes, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        message.Headers.TryAddWithoutValidation("User-Agent", "NextCoWork/3.x");
        foreach (var (name, value) in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value) && request.Body is not null)
            {
                message.Content ??= new StringContent(request.Body);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (request.Body is not null && message.Content is null)
        {
            message.Content = new StringContent(request.Body);
        }

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        using var buffer = new MemoryStream();
        var truncated = false;
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[64 * 1024];
            for (;;)
            {
                var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var room = maxBodyBytes - (int)buffer.Length;
                if (read >= room)
                {
                    buffer.Write(chunk, 0, Math.Max(room, 0));
                    truncated = true;
                    break; // 到上限就停(而不是读完再看:一个声称 1MB、实际 2GB 的响应已经把内存吃光了)
                }

                buffer.Write(chunk, 0, read);
            }
        }

        return new PluginHttpResponse((int)response.StatusCode, headers, buffer.ToArray(), truncated);
    }
}
