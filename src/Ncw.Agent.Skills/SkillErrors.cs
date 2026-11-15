namespace Ncw.Agent.Skills;

/// <summary>
/// 安装 / 开关 / 市场操作的稳定错误 key(2.x 的 <c>installMessageKey</c> 对应)。控制端按 key 翻译成界面文字;异常消息是英文,只给日志,
/// <b>不含用户路径以外的敏感信息</b>(2.x 把「ZIP 包含非法路径」这种给开发者看的诊断直接画在卡片上,这里不再)。
/// </summary>
public static class SkillErrorKeys
{
    public const string PackageTooLarge = "skills.packageTooLarge";
    public const string ZipInvalid = "skills.zipInvalid";
    public const string SkillMdInvalid = "skills.skillMdInvalid";
    public const string DigestMismatch = "skills.digestMismatch";
    public const string NoVersion = "skills.noVersion";
    public const string VersionUnavailable = "skills.versionUnavailable";
    public const string NotLoaded = "skills.notLoaded";
    public const string NetworkFailed = "skills.networkFailed";
    public const string DiskPermission = "skills.diskPermission";
    public const string DiskFull = "skills.diskFull";
    public const string AuthRequired = "skills.authRequired";
    public const string ScopeRequired = "skills.scopeRequired";
    public const string ClientAssetsUnavailable = "skills.clientAssetsUnavailable";
    public const string WorkspaceRequired = "skills.workspaceRequired";
    public const string WorkspaceNotFound = "skills.workspaceNotFound";
    public const string NotFound = "skills.notFound";
    public const string InvalidLocation = "skills.invalidLocation";
    public const string OperationFailed = "skills.operationFailed";
}

/// <summary>Skill 域的业务失败:<see cref="Key"/> 是稳定 key,<see cref="Parameters"/> 是翻译用的结构化参数(不含凭据)。Host 转成协议错误。</summary>
public sealed class SkillException(string key, string message, IReadOnlyDictionary<string, string>? parameters = null, Exception? inner = null) : Exception(message, inner)
{
    public string Key { get; } = key;

    public IReadOnlyDictionary<string, string> Parameters { get; } = parameters ?? new Dictionary<string, string>();
}
