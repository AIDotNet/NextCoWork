using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Files;

/// <summary>文件服务的错误码。和协议里的 <c>files.*</c> 稳定 key 一一对应(映射在 Host);本层不产出界面文字,也不引用协议层。</summary>
public enum FilesErrorCode
{
    NotFound,
    Exists,
    Conflict,
    InvalidPath,
    Symlink,
    Permission,
    TooLarge,
    NotFile,
    InvalidEncoding,
    Unsupported,
    Io,
    /// <summary>项目根读不到(被删、被移走、盘没挂上)。</summary>
    ProjectUnavailable,
    InvalidPattern,
    InvalidName,
    /// <summary>资源内容不合法(缺必填字段、工具名不认识……)。<see cref="FilesException.Detail"/> 是英文诊断。</summary>
    InvalidResource,
    BuiltinReadOnly,
    /// <summary>执行环境断开(SSH 重连中):可重试。</summary>
    Disconnected,
}

/// <summary>文件服务的失败。<see cref="Detail"/> 是英文诊断(不含路径),只给日志。</summary>
public sealed class FilesException(FilesErrorCode code, string? detail = null, Exception? inner = null)
    : Exception(detail is null ? $"files:{code}" : $"files:{code} {detail}", inner)
{
    public FilesErrorCode Code { get; } = code;

    public string? Detail { get; } = detail;

    /// <summary>把环境层的错误译成文件服务的错误码;已经是 <see cref="FilesException"/> 的原样返回。</summary>
    public static FilesException From(Exception e) => e switch
    {
        FilesException f => f,
        EnvironmentException env => new FilesException(Map(env.Code), env.Code.ToString(), env),
        _ => new FilesException(FilesErrorCode.Io, e.GetType().Name, e),
    };

    private static FilesErrorCode Map(EnvironmentErrorCode code) => code switch
    {
        EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory => FilesErrorCode.NotFound,
        EnvironmentErrorCode.AlreadyExists or EnvironmentErrorCode.DirectoryNotEmpty => FilesErrorCode.Exists,
        EnvironmentErrorCode.IsDirectory => FilesErrorCode.NotFile,
        EnvironmentErrorCode.PermissionDenied => FilesErrorCode.Permission,
        EnvironmentErrorCode.TooLarge => FilesErrorCode.TooLarge,
        EnvironmentErrorCode.InvalidPath or EnvironmentErrorCode.PathEscape => FilesErrorCode.InvalidPath,
        EnvironmentErrorCode.Unsupported or EnvironmentErrorCode.UnsupportedPlatform => FilesErrorCode.Unsupported,
        EnvironmentErrorCode.Conflict => FilesErrorCode.Conflict,
        EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.StaleGeneration or EnvironmentErrorCode.ConnectionFailed
            or EnvironmentErrorCode.ResultUnknown or EnvironmentErrorCode.Timeout or EnvironmentErrorCode.Cancelled => FilesErrorCode.Disconnected,
        _ => FilesErrorCode.Io,
    };
}
