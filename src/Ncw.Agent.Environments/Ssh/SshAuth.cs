using Renci.SshNet;
using Renci.SshNet.Common;

namespace Ncw.Agent.Environments.Ssh;

/// <summary>认证方式的选择策略(纯函数,单测直接覆盖)。</summary>
public static class SshAuthPlan
{
    public static bool UsesKeys(SshAuthMethod method) => method is SshAuthMethod.Auto or SshAuthMethod.Key;

    public static bool UsesKeyboardInteractive(SshAuthMethod method) => method is SshAuthMethod.Auto or SshAuthMethod.Interactive;

    public static bool UsesPassword(SshAuthMethod method) => method is SshAuthMethod.Auto or SshAuthMethod.Password or SshAuthMethod.Ask;

    /// <summary>总是在连接前先问密码的方式(用户明确选了密码)。<see cref="SshAuthMethod.Auto"/> 只在其它方式都被拒绝之后才问。</summary>
    public static bool AsksPasswordUpFront(SshAuthMethod method) => method is SshAuthMethod.Password or SshAuthMethod.Ask;

    /// <summary>没有显式私钥时尝试的默认文件(OpenSSH 的默认身份文件的常用子集,按优先顺序)。</summary>
    public static IReadOnlyList<string> DefaultIdentityFiles(string home) =>
        [Path.Combine(home, ".ssh", "id_ed25519"), Path.Combine(home, ".ssh", "id_ecdsa"), Path.Combine(home, ".ssh", "id_rsa")];
}

/// <summary>一次连接尝试的认证方法集合。私钥文件在这里读取(设备本地磁盘,属于执行环境实现自身的 IO)。</summary>
internal sealed class SshAuth
{
    private const int MaxPassphraseAttempts = 3;
    private readonly List<AuthenticationMethod> _methods = [];

    private SshAuth()
    {
    }

    /// <summary>用户在键盘交互式提示里取消了。认证随后会失败,调用方据此把「认证失败」改报成「已取消」。</summary>
    public bool CancelRequested { get; private set; }

    public AuthenticationMethod[] Methods() => [.. _methods];

    public static string CacheKey(SshHop hop) => $"{hop.User}@{hop.HostKeyName}";

    public static bool CanAskPasswordAfterFailure(SshConnectContext context) =>
        context.Services.Credentials is not null && SshAuthPlan.UsesPassword(context.Profile.AuthMethod);

    /// <summary>只含 <c>none</c> 方法的认证集合,用来探测服务器接受哪些认证方式。</summary>
    public static SshAuth ProbeOnly(SshHop hop)
    {
        var auth = new SshAuth();
        auth._methods.Add(new NoneAuthenticationMethod(hop.User));
        return auth;
    }

    /// <summary>
    /// 失败消息(<c>... authentication (publickey,password).</c>)里括号中是服务器接受的方式。解析不出来(消息格式变了)时按「接受」处理。
    /// </summary>
    public static bool MessageMentionsPassword(string? message)
    {
        message ??= "";
        var open = message.LastIndexOf('(');
        var close = message.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return true;
        }

        return message[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries).Contains("password", StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<SshAuth> BuildAsync(SshHop hop, SshConnectContext context, CancellationToken ct)
    {
        var auth = new SshAuth();
        var method = context.Profile.AuthMethod;
        var provider = context.Services.Credentials;
        var cacheKey = CacheKey(hop);

        if (SshAuthPlan.UsesKeys(method))
        {
            var keys = new List<IPrivateKeySource>();
            var agentBlobs = new List<byte[]>();
            // ssh-agent 的身份排在私钥文件前面(和 OpenSSH 一致);IdentitiesOnly 且指定了私钥文件时只用那些文件
            if (context.Options.Agent is { } agent && !(hop.IdentitiesOnly && hop.IdentityFiles.Count > 0))
            {
                foreach (var identity in await ListAgentIdentitiesAsync(agent, ct).ConfigureAwait(false))
                {
                    var source = new AgentKeySource(identity, agent);
                    if (source.HostKeyAlgorithms.Count > 0)
                    {
                        keys.Add(source);
                        agentBlobs.Add(identity.PublicKeyBlob);
                    }
                }
            }

            var candidates = hop.IdentityFiles.Count > 0 ? hop.IdentityFiles : SshAuthPlan.DefaultIdentityFiles(context.Services.ConfigFiles.Home);
            foreach (var path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                // agent 已经持有这把私钥(配套的 .pub 能对上):让 agent 签名,不为文件里的私钥口令打扰用户
                if (agentBlobs.Count > 0 && PublicKeyBlobOf(path + ".pub") is { } blob && agentBlobs.Any(b => b.AsSpan().SequenceEqual(blob)))
                {
                    continue;
                }

                if (await LoadKeyAsync(path, hop, context, ct).ConfigureAwait(false) is { } key)
                {
                    keys.Add(key);
                }
            }

            if (keys.Count > 0)
            {
                auth._methods.Add(new PrivateKeyAuthenticationMethod(hop.User, [.. keys]));
            }
        }

        if (SshAuthPlan.AsksPasswordUpFront(method) && provider is not null && !context.Credentials.Passwords.ContainsKey(cacheKey))
        {
            var answer = await provider.AskAsync(new SshCredentialRequest(SshCredentialKind.Password, hop.HostKeyName, hop.User), ct).ConfigureAwait(false);
            if (answer is null || answer.Count == 0)
            {
                throw new EnvironmentException(EnvironmentErrorCode.Cancelled);
            }

            context.Credentials.Passwords[cacheKey] = answer[0];
        }

        var password = SshAuthPlan.UsesPassword(method) && context.Credentials.Passwords.TryGetValue(cacheKey, out var cached) ? cached : null;

        if ((SshAuthPlan.UsesKeyboardInteractive(method) && provider is not null) || (password is not null && SshAuthPlan.AsksPasswordUpFront(method)))
        {
            var interactive = new KeyboardInteractiveAuthenticationMethod(hop.User);
            interactive.AuthenticationPrompt += (_, e) => auth.Answer(e, hop, provider, SshAuthPlan.AsksPasswordUpFront(method) ? password : null);
            auth._methods.Add(interactive);
        }

        if (password is not null)
        {
            auth._methods.Add(new PasswordAuthenticationMethod(hop.User, password));
        }

        if (auth._methods.Count == 0)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Authentication, "no usable authentication method");
        }

        return auth;
    }

    /// <summary>agent 没起来、不可达、拒绝列表:都当作「没有 agent」,不阻止其它认证方式。</summary>
    private static async Task<IReadOnlyList<SshAgentIdentity>> ListAgentIdentitiesAsync(ISshAgent agent, CancellationToken ct)
    {
        try
        {
            return await agent.ListIdentitiesAsync(ct).ConfigureAwait(false);
        }
        catch (EnvironmentException)
        {
            return [];
        }
    }

    /// <summary>读 OpenSSH 的 <c>.pub</c> 文件(<c>type base64 [comment]</c>)里的公钥 blob;不存在或读不懂返回 null。</summary>
    private static byte[]? PublicKeyBlobOf(string pubPath)
    {
        try
        {
            var parts = File.ReadAllText(pubPath).Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length >= 2 ? Convert.FromBase64String(parts[1]) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static async Task<PrivateKeyFile?> LoadKeyAsync(string path, SshHop hop, SshConnectContext context, CancellationToken ct)
    {
        var provider = context.Services.Credentials;
        context.Credentials.Passphrases.TryGetValue(path, out var passphrase);
        for (var attempt = 0; attempt < MaxPassphraseAttempts; attempt++)
        {
            var rejected = false;
            try
            {
                return passphrase is null ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
            }
            catch (SshException) when (passphrase is not null)
            {
                // 口令不对
                rejected = true;
                context.Credentials.Passphrases.Remove(path);
            }
            catch (Exception e) when (e is SshException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
            {
                // 读不了/不支持的私钥格式:跳过这把,不阻止其它认证方式
                return null;
            }

            if (provider is null)
            {
                return null;
            }

            var answer = await provider.AskAsync(
                new SshCredentialRequest(SshCredentialKind.KeyPassphrase, hop.HostKeyName, hop.User, path, PreviousRejected: rejected), ct).ConfigureAwait(false);
            if (answer is null || answer.Count == 0)
            {
                throw new EnvironmentException(EnvironmentErrorCode.Cancelled, path);
            }

            passphrase = answer[0];
            context.Credentials.Passphrases[path] = passphrase;
        }

        return null;
    }

    /// <summary>
    /// 键盘交互式的回答。SSH.NET 的回调是同步的,这里在线程池线程上阻塞等待回答(此时握手线程本来就在等我们)。
    /// 用户明确选了密码时,第一个问题直接用已知的密码回答(PAM 把「密码」做成键盘交互式提示的服务器很常见)。
    /// </summary>
    private void Answer(AuthenticationPromptEventArgs e, SshHop hop, ISshCredentialProvider? provider, string? password)
    {
        var prompts = e.Prompts.ToList();
        if (prompts.Count == 0)
        {
            return;
        }

        if (password is not null && prompts.Count == 1)
        {
            prompts[0].Response = password;
            return;
        }

        if (provider is null)
        {
            return;
        }

        var request = new SshCredentialRequest(SshCredentialKind.KeyboardInteractive, hop.HostKeyName, hop.User,
            e.Instruction, [.. prompts.Select(p => new SshPrompt(p.Request, p.IsEchoed))]);
        var answers = Task.Run(() => provider.AskAsync(request, CancellationToken.None).AsTask()).GetAwaiter().GetResult();
        if (answers is null)
        {
            CancelRequested = true;
            return;
        }

        for (var i = 0; i < prompts.Count && i < answers.Count; i++)
        {
            prompts[i].Response = answers[i];
        }
    }
}
