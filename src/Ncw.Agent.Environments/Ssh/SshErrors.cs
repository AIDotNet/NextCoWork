using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Ncw.Agent.Environments.Ssh;

internal static class SshErrors
{
    /// <summary>是不是「连接本身出问题」(而不是某个操作被服务器拒绝)。</summary>
    public static bool IsConnectionLoss(Exception e) => e is SshConnectionException or SocketException or ObjectDisposedException
        or SshOperationTimeoutException or ProxyException
        || (e is IOException { InnerException: SocketException or SshConnectionException })
        || (e is InvalidOperationException && e.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase))
        || (e is SshException && e.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把 SSH.NET / SFTP 的异常映射成结构化错误。写类操作在连接中断时结果未知(<see cref="EnvironmentErrorCode.ResultUnknown"/>),
    /// 不能报成「没写」;读类操作报 <see cref="EnvironmentErrorCode.Disconnected"/>。
    /// </summary>
    public static EnvironmentException Map(Exception e, string? path, bool mutating)
    {
        switch (e)
        {
            case EnvironmentException env:
                return env;
            case SftpPathNotFoundException:
                return new EnvironmentException(EnvironmentErrorCode.NotFound, path, e);
            case SftpPermissionDeniedException:
                return new EnvironmentException(EnvironmentErrorCode.PermissionDenied, path, e);
            case SshAuthenticationException:
                return new EnvironmentException(EnvironmentErrorCode.Authentication, null, e);
            case SshOperationTimeoutException:
            case TimeoutException:
                return new EnvironmentException(EnvironmentErrorCode.Timeout, path, e);
        }

        if (IsConnectionLoss(e))
        {
            return new EnvironmentException(mutating ? EnvironmentErrorCode.ResultUnknown : EnvironmentErrorCode.Disconnected, path, e);
        }

        return new EnvironmentException(EnvironmentErrorCode.Unsupported, path is null ? e.Message : $"{path}: {e.Message}", e);
    }

    /// <summary>建立连接阶段的异常映射。</summary>
    public static EnvironmentException MapConnect(Exception e) => e switch
    {
        EnvironmentException env => env,
        SshAuthenticationException => new EnvironmentException(EnvironmentErrorCode.Authentication, null, e),
        SshOperationTimeoutException or TimeoutException => new EnvironmentException(EnvironmentErrorCode.Timeout, null, e),
        _ => new EnvironmentException(EnvironmentErrorCode.ConnectionFailed, e.Message, e),
    };
}
