namespace Ncw.Agent.Environments.Ssh;

public enum SshCredentialKind
{
    Password,

    /// <summary>私钥的口令。<see cref="SshCredentialRequest.Detail"/> 是私钥文件路径。</summary>
    KeyPassphrase,

    /// <summary>键盘交互式认证(两步验证等):<see cref="SshCredentialRequest.Prompts"/> 是服务器给的问题。</summary>
    KeyboardInteractive,
}

public sealed record SshPrompt(string Text, bool Echo);

/// <param name="HostName">在向哪一跳要凭据(多跳时每跳都不同)。</param>
/// <param name="PreviousRejected">这次是因为上一次给的(含已保存的)被拒绝才再问的:界面要把这件事说出来,否则用户会以为「我明明存了密码它还问」是功能坏了。</param>
public sealed record SshCredentialRequest(
    SshCredentialKind Kind,
    string HostName,
    string UserName,
    string? Detail = null,
    IReadOnlyList<SshPrompt>? Prompts = null,
    bool PreviousRejected = false);

/// <summary>
/// 凭据提示也是交互(06 §1.4):SSH 密码、私钥口令、键盘交互式两步验证都走这个回调,和审批走同一套机制,
/// 所以任何已连接的控制端(包括手机)都能回答,先到先得。本层不产出界面文字,只给结构化的请求。
/// </summary>
public interface ISshCredentialProvider
{
    /// <summary>
    /// 返回答案(密码/口令是一项,键盘交互式每个问题一项);<c>null</c> 表示用户取消,连接以 <see cref="EnvironmentErrorCode.Cancelled"/> 失败。
    /// 是否「记住」由实现自己决定(保存到 NCK1 凭据库)。
    /// </summary>
    ValueTask<IReadOnlyList<string>?> AskAsync(SshCredentialRequest request, CancellationToken ct);
}

/// <summary>ssh-agent 里的一把身份。</summary>
public sealed record SshAgentIdentity(byte[] PublicKeyBlob, string Comment);

/// <summary>
/// ssh-agent 协议客户端抽象(Unix 上的 <c>SSH_AUTH_SOCK</c>、Windows 上的 OpenSSH 命名管道)。实现是 <see cref="SshAgentClient"/>;
/// 与 SSH.NET 的公钥认证的对接见 <see cref="SshEnvironmentOptions.Agent"/>(把签名转给 agent 的 <c>HostAlgorithm</c>)。
/// </summary>
public interface ISshAgent : IAsyncDisposable
{
    ValueTask<IReadOnlyList<SshAgentIdentity>> ListIdentitiesAsync(CancellationToken ct);

    ValueTask<byte[]> SignAsync(byte[] publicKeyBlob, byte[] data, uint flags, CancellationToken ct);
}

/// <summary>断线重连策略:指数退避加抖动。</summary>
public sealed record ReconnectPolicy
{
    public static ReconnectPolicy Default { get; } = new();

    /// <summary>不自动重连(测试和一次性连接用)。</summary>
    public static ReconnectPolicy Never { get; } = new() { MaxAttempts = 0 };

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    public double Factor { get; init; } = 2;

    /// <summary>相对抖动幅度:0.2 = 在 ±20% 内随机,避免多个环境同时重连时同步冲击服务器。</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>最多连续尝试多少次,<c>null</c> = 不限(靠用户关闭环境停下)。</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>第 <paramref name="attempt"/> 次(从 1 开始)尝试前等多久。<paramref name="random"/> 取 [0,1)。</summary>
    public TimeSpan DelayBefore(int attempt, double random)
    {
        var exponent = Math.Max(0, attempt - 1);
        var seconds = Math.Min(MaxDelay.TotalSeconds, InitialDelay.TotalSeconds * Math.Pow(Factor, exponent));
        var spread = 1 + ((random * 2) - 1) * Jitter;
        return TimeSpan.FromSeconds(Math.Max(0, seconds * spread));
    }

    /// <summary>失败之后是否还该再试:认证失败、指纹变化/被拒绝、配置错误重试没有意义(只会反复打扰用户或被服务器封禁)。</summary>
    public bool ShouldRetry(int attemptsSoFar, EnvironmentErrorCode? lastError)
    {
        if (MaxAttempts is { } max && attemptsSoFar >= max)
        {
            return false;
        }

        return lastError is not (EnvironmentErrorCode.Authentication or EnvironmentErrorCode.HostKeyChanged
            or EnvironmentErrorCode.HostKeyRejected or EnvironmentErrorCode.InvalidProfile or EnvironmentErrorCode.Cancelled
            or EnvironmentErrorCode.UnsupportedPlatform or EnvironmentErrorCode.Unsupported);
    }
}
