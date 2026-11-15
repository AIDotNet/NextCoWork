namespace Ncw.Agent.Environments;

/// <summary>
/// 环境层的错误码(移植自 2.x 的 <c>EnvironmentErrorCode</c>,按 3.0 补了几个文件系统和主机密钥的细分)。
/// 本层不产出界面文字:上层按这个码和 <see cref="EnvironmentException.Detail"/> 生成文案。
/// </summary>
public enum EnvironmentErrorCode
{
    InvalidProfile,
    InvalidPath,
    PathEscape,
    NotFound,
    AlreadyExists,
    NotDirectory,
    IsDirectory,
    DirectoryNotEmpty,
    PermissionDenied,
    TooLarge,
    Unsupported,
    Conflict,
    Cancelled,
    Timeout,
    ProcessStartFailed,
    ConnectionFailed,
    Disconnected,
    /// <summary>操作发起时的连接代(generation)已经过期:断线重连后,旧代发起的操作一律失败。</summary>
    StaleGeneration,
    Authentication,
    /// <summary>指纹和已钉扎的不一致。直接拒绝,不给「继续」的选项。</summary>
    HostKeyChanged,
    /// <summary>用户(或没有确认回调)拒绝了首次出现的主机指纹。</summary>
    HostKeyRejected,
    SftpUnavailable,
    UnsupportedPlatform,
    EnvironmentNotFound,
    /// <summary>写类操作在连接中断时已经发出,结果未知(不能当成没写)。</summary>
    ResultUnknown,
}

public sealed class EnvironmentException : Exception
{
    public EnvironmentException(EnvironmentErrorCode code, string? detail = null, Exception? inner = null)
        : base(detail is null ? $"environment:{code}" : $"environment:{code} {detail}", inner)
    {
        Code = code;
        Detail = detail;
    }

    public EnvironmentErrorCode Code { get; }

    public string? Detail { get; }
}
