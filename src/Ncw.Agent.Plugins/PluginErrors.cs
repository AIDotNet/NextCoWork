namespace Ncw.Agent.Plugins;

/// <summary>
/// 插件域的业务失败。<see cref="Key"/> 是稳定的 <c>plugins.*</c> key(控制端翻译,Host 放进 <c>error.details</c>),<see cref="Exception.Message"/> 是英文,给日志和排错。
/// 参数里不得含任何凭据。
/// </summary>
public sealed class PluginException(string key, string message, IReadOnlyDictionary<string, string>? parameters = null, Exception? inner = null) : Exception(message, inner)
{
    public string Key { get; } = key;

    public IReadOnlyDictionary<string, string>? Parameters { get; } = parameters;
}

public static class PluginErrorKeys
{
    public const string NotFound = "plugins.notFound";
    public const string NotLoaded = "plugins.notLoaded";
    public const string PackageTooLarge = "plugins.packageTooLarge";
    public const string ZipInvalid = "plugins.zipInvalid";
    public const string ManifestInvalid = "plugins.manifestInvalid";
    public const string EngineIncompatible = "plugins.engineIncompatible";
    public const string DigestMismatch = "plugins.digestMismatch";
    public const string PackageFileMissing = "plugins.packageFileMissing";
    public const string AuthRequired = "plugins.authRequired";
    public const string ScopeRequired = "plugins.scopeRequired";
    public const string VersionUnavailable = "plugins.versionUnavailable";
    public const string NoVersion = "plugins.noVersion";
    public const string MarketFailed = "plugins.marketFailed";
    public const string DiskPermission = "plugins.diskPermission";
    public const string NotRunnable = "plugins.notRunnable";
    public const string Faulted = "plugins.faulted";
    public const string CommandUndeclared = "plugins.commandUndeclared";
    public const string CommandFailed = "plugins.commandFailed";
    public const string PermissionOutsideManifest = "plugins.permissionOutsideManifest";
    public const string InvalidArgument = "plugins.invalidArgument";
    public const string InteractionNotFound = "plugins.interactionNotFound";
    public const string ConfigurationInvalid = "plugins.configurationInvalid";
    public const string OperationFailed = "plugins.operationFailed";
}

/// <summary>插件诊断(装载 / 运行期问题)的稳定 key。诊断里的 <c>Params</c> 是结构化参数,<c>Message</c> 是英文。</summary>
public static class PluginDiagnosticKeys
{
    public const string ManifestWarning = "plugins.diag.manifestWarning";
    public const string EngineDeprecated = "plugins.diag.engineDeprecated";
    public const string EngineIncompatible = "plugins.diag.engineIncompatible";
    public const string Unsupported = "plugins.diag.unsupported";
    public const string MainMissing = "plugins.diag.mainMissing";
    public const string CompileFailed = "plugins.diag.compileFailed";
    public const string ActivateFailed = "plugins.diag.activateFailed";
    public const string ActivateTimeout = "plugins.diag.activateTimeout";
    public const string PermissionDenied = "plugins.diag.permissionDenied";
    public const string ToolUndeclared = "plugins.diag.toolUndeclared";
    public const string CommandUndeclared = "plugins.diag.commandUndeclared";
    public const string ToolNameClash = "plugins.diag.toolNameClash";
    public const string Faulted = "plugins.diag.faulted";
    public const string UnhandledRejection = "plugins.diag.unhandledRejection";
    public const string InterceptorTimeout = "plugins.diag.interceptorTimeout";
    public const string InterceptorFailed = "plugins.diag.interceptorFailed";
    public const string ContextFailed = "plugins.diag.contextFailed";
    public const string PluginLog = "plugins.diag.log";
    public const string PendingApproval = "plugins.diag.pendingApproval";
    public const string SkillMissing = "plugins.diag.skillMissing";
    public const string DependencyMissing = "plugins.diag.dependencyMissing";
}
