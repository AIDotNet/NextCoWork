using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using TKCode.Arc.Localization;

namespace Ncw.UI.Localization;

/// <summary>
/// 把设备给的稳定字符串(枚举值、错误 key)翻译成当前语言。约定:枚举值 <c>spawn-failed</c> / <c>streamable-http</c> 按 Pascal 写法拼在前缀后面,
/// 对应 <see cref="NcwTextKind"/> 的成员(<c>HookOutcomeSpawnFailed</c>);认不出来的值原样显示(设备以后加了新值,界面不会崩,也不会显示成空)。
/// </summary>
public static class KeyText
{
    private static readonly Dictionary<string, string> Special = new(StringComparer.Ordinal) { ["xhigh"] = "XHigh" };

    /// <summary><c>prefix</c> + Pascal(<c>value</c>) 对应的文字;没有这个成员时返回 <paramref name="value"/>。</summary>
    public static string Get(string prefix, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return Enum.TryParse<NcwTextKind>(prefix + Pascal(value), out var kind) && Enum.IsDefined(kind) ? NcwTexts.Get(kind) : value;
    }

    public static string Pascal(string value)
    {
        if (Special.TryGetValue(value, out var special))
        {
            return special;
        }

        return string.Concat(value.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>设备给的失败原因 → 文字:先按 key 精确翻译,认不出来按结局给通用说明(永远不显示成空,也不把英文诊断直接丢给用户)。</summary>
    public static string Error(string? key, Ncw.Client.Actions.ActionOutcome outcome = Ncw.Client.Actions.ActionOutcome.Failed)
    {
        if (key is not null && ErrorKeys.TryGetValue(key, out var known))
        {
            return NcwTexts.Get(known);
        }

        if (key is not null)
        {
            foreach (var (prefix, kind) in ErrorPrefixes)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return NcwTexts.Get(kind);
                }
            }
        }

        return NcwTexts.Get(outcome switch
        {
            Ncw.Client.Actions.ActionOutcome.Unavailable => NcwTextKind.ErrDeviceUnavailable,
            Ncw.Client.Actions.ActionOutcome.Forbidden => NcwTextKind.ErrForbidden,
            Ncw.Client.Actions.ActionOutcome.NotFound => NcwTextKind.ErrNotFound,
            Ncw.Client.Actions.ActionOutcome.Invalid => NcwTextKind.ErrInvalid,
            Ncw.Client.Actions.ActionOutcome.Conflict => NcwTextKind.ErrConflict,
            Ncw.Client.Actions.ActionOutcome.Unsupported => NcwTextKind.ErrUnsupported,
            _ => NcwTextKind.ErrGeneric,
        });
    }

    public static string Error(Failure? failure) => failure is null ? "" : Error(failure.Key, failure.Outcome);

    /// <summary>加载诊断(Skill / 命令 / Hook 配置哪里不对)的稳定 key → 文字:按原因归类,具体路径另行显示。</summary>
    public static string Diagnostic(string key) => NcwTexts.Get(key switch
    {
        _ when key.Contains("nreadable", StringComparison.Ordinal) => NcwTextKind.DiagUnreadable,
        _ when key.Contains("timeout", StringComparison.OrdinalIgnoreCase) => NcwTextKind.DiagTimeout,
        _ when key.Contains("spawnFailed", StringComparison.Ordinal) => NcwTextKind.DiagSpawnFailed,
        _ when key.Contains("tooMany", StringComparison.Ordinal) || key.Contains("tooLarge", StringComparison.Ordinal) => NcwTextKind.DiagTooMany,
        _ when key.Contains("outsideRoot", StringComparison.Ordinal) => NcwTextKind.DiagOutsideRoot,
        _ when key.Contains("nvalid", StringComparison.Ordinal) || key.Contains("rontmatter", StringComparison.Ordinal) || key.Contains("issing", StringComparison.Ordinal)
            || key.Contains("mpty", StringComparison.Ordinal) || key.Contains("ismatch", StringComparison.Ordinal) => NcwTextKind.DiagInvalid,
        _ => NcwTextKind.DiagGeneric,
    });

    private static readonly Dictionary<string, NcwTextKind> ErrorKeys = new(StringComparer.Ordinal)
    {
        // data(设置 · 数据 · 回退到 2.x)
        ["data.rollback.unavailable"] = NcwTextKind.ErrDataRollbackUnavailable,
        // skills
        ["skills.authRequired"] = NcwTextKind.ErrSkillsAuthRequired,
        ["skills.packageTooLarge"] = NcwTextKind.ErrSkillsPackageTooLarge,
        ["skills.zipInvalid"] = NcwTextKind.ErrSkillsZipInvalid,
        ["skills.skillMdMissing"] = NcwTextKind.ErrSkillsZipInvalid,
        ["skills.skillMdInvalid"] = NcwTextKind.ErrSkillsZipInvalid,
        ["skills.frontmatter"] = NcwTextKind.ErrSkillsZipInvalid,
        ["skills.nameMismatch"] = NcwTextKind.ErrSkillsZipInvalid,
        ["skills.nameInvalid"] = NcwTextKind.ErrSkillsZipInvalid,
        ["skills.clientAssetsUnavailable"] = NcwTextKind.ErrSkillsClientAssets,
        ["skills.networkFailed"] = NcwTextKind.ErrSkillsNetwork,
        ["skills.digestMismatch"] = NcwTextKind.ErrSkillsDigest,
        ["skills.diskFull"] = NcwTextKind.ErrSkillsDisk,
        ["skills.diskPermission"] = NcwTextKind.ErrSkillsDisk,
        ["skills.notFound"] = NcwTextKind.ErrNotFound,
        ["skills.pluginNameClash"] = NcwTextKind.ErrSkillsPluginClash,
        ["skills.workspaceRequired"] = NcwTextKind.ErrNeedsProject,
        ["skills.workspaceNotFound"] = NcwTextKind.ErrNeedsProject,
        // hooks
        ["hooks.error.badEvent"] = NcwTextKind.ErrHooksBadEvent,
        ["hooks.error.badMatcher"] = NcwTextKind.ErrHooksBadMatcher,
        ["hooks.error.badTimeout"] = NcwTextKind.ErrHooksBadTimeout,
        ["hooks.error.timeoutTooLong"] = NcwTextKind.ErrHooksBadTimeout,
        ["hooks.error.emptyCommand"] = NcwTextKind.ErrHooksEmptyCommand,
        ["hooks.error.emptyPrompt"] = NcwTextKind.ErrHooksEmptyPrompt,
        ["hooks.error.tooLong"] = NcwTextKind.ErrHooksTooLong,
        ["hooks.error.badEnv"] = NcwTextKind.ErrHooksBadEnv,
        ["hooks.error.fileUnreadable"] = NcwTextKind.ErrHooksFileUnreadable,
        ["hooks.error.writeFailed"] = NcwTextKind.ErrHooksWriteFailed,
        ["hooks.error.noWorkspace"] = NcwTextKind.ErrNeedsProject,
        ["hooks.error.notFound"] = NcwTextKind.ErrNotFound,
        ["hooks.error.full"] = NcwTextKind.ErrHooksTooMany,
        ["hooks.warn.weakMatcher"] = NcwTextKind.WarnHooksWeakMatcher,
        // mcp
        ["mcp.invalidId"] = NcwTextKind.ErrMcpInvalidId,
        ["mcp.nameInvalid"] = NcwTextKind.ErrMcpNameInvalid,
        ["mcp.commandRequired"] = NcwTextKind.ErrMcpCommand,
        ["mcp.commandInvalid"] = NcwTextKind.ErrMcpCommand,
        ["mcp.urlInvalid"] = NcwTextKind.ErrMcpUrl,
        ["mcp.urlScheme"] = NcwTextKind.ErrMcpUrl,
        ["mcp.urlCredentials"] = NcwTextKind.ErrMcpUrlCredentials,
        ["mcp.secretNameInvalid"] = NcwTextKind.ErrMcpSecretName,
        ["mcp.transportFieldMismatch"] = NcwTextKind.ErrMcpTransportMismatch,
        ["mcp.tooManyItems"] = NcwTextKind.ErrMcpTooMany,
        ["mcp.workspaceInvalid"] = NcwTextKind.ErrNeedsProject,
        ["mcp.notConnected"] = NcwTextKind.ErrMcpNotConnected,
        ["mcp.notFound"] = NcwTextKind.ErrNotFound,
        ["mcp.connect.timeout"] = NcwTextKind.ErrMcpConnTimeout,
        ["mcp.connect.commandNotFound"] = NcwTextKind.ErrMcpConnCommandNotFound,
        ["mcp.connect.processExited"] = NcwTextKind.ErrMcpConnExited,
        ["mcp.connect.startFailed"] = NcwTextKind.ErrMcpConnExited,
        ["mcp.connect.unauthorized"] = NcwTextKind.ErrMcpConnUnauthorized,
        ["mcp.connect.network"] = NcwTextKind.ErrMcpConnNetwork,
        ["mcp.connect.http"] = NcwTextKind.ErrMcpConnNetwork,
        ["mcp.connect.projectMissing"] = NcwTextKind.ErrNeedsProject,
        ["mcp.connect.environmentUnavailable"] = NcwTextKind.ErrMcpConnEnvironment,
        ["mcp.connect.protocol"] = NcwTextKind.ErrMcpConnProtocol,
        ["mcp.connect.protocolVersion"] = NcwTextKind.ErrMcpConnProtocol,
        // storage
        ["storage.busy"] = NcwTextKind.ErrStorageBusy,
        ["storage.confirmationRequired"] = NcwTextKind.ErrStorageConfirmation,
        ["storage.backup.noDirectory"] = NcwTextKind.ErrStorageNoDirectory,
        ["storage.backup.directoryInvalid"] = NcwTextKind.ErrStorageDirectory,
        ["storage.backup.directoryOverlaps"] = NcwTextKind.ErrStorageDirectory,
        ["storage.backup.directoryUnwritable"] = NcwTextKind.ErrStorageDirectory,
        ["storage.backup.notFound"] = NcwTextKind.ErrStorageBackupFile,
        ["storage.backup.unreadable"] = NcwTextKind.ErrStorageBackupFile,
        ["storage.import.notJson"] = NcwTextKind.ErrStorageImportFile,
        ["storage.import.unsupported"] = NcwTextKind.ErrStorageImportFile,
        ["storage.import.version"] = NcwTextKind.ErrStorageImportFile,
        ["storage.transfer.tooLarge"] = NcwTextKind.ErrStorageImportFile,
        // settings
        ["settings.invalid.range"] = NcwTextKind.ErrSettingsRange,
        ["settings.invalid.choice"] = NcwTextKind.ErrSettingsChoice,
        ["settings.invalid.model"] = NcwTextKind.ErrSettingsModel,
        ["settings.invalid.path"] = NcwTextKind.ErrSettingsPath,
        ["settings.invalid.url"] = NcwTextKind.ErrSettingsUrl,
        ["settings.invalid.tooLong"] = NcwTextKind.ErrSettingsTooLong,
        ["settings.localOnly"] = NcwTextKind.ErrLocalOnly,
        ["settings.projectNotFound"] = NcwTextKind.ErrNeedsProject,
        // schedules
        ["schedules.invalid.name"] = NcwTextKind.SchedProblemName,
        ["schedules.invalid.prompt"] = NcwTextKind.SchedProblemPrompt,
        ["schedules.invalid.rule"] = NcwTextKind.SchedProblemRule,
        ["schedules.invalid.timezone"] = NcwTextKind.SchedProblemTimezone,
        ["schedules.invalid.repeat"] = NcwTextKind.SchedProblemRepeat,
        ["schedules.invalid.neverFires"] = NcwTextKind.SchedProblemNeverFires,
        ["schedules.notFound"] = NcwTextKind.ErrNotFound,
        ["schedules.alreadyRunning"] = NcwTextKind.ErrSchedAlreadyRunning,
        ["schedules.runActive"] = NcwTextKind.ErrSchedAlreadyRunning,
        ["schedules.workspaceNotFound"] = NcwTextKind.ErrNeedsProject,
        ["scheduled.missed"] = NcwTextKind.SchedErrMissed,
        ["scheduled.overlap"] = NcwTextKind.SchedErrOverlap,
        ["scheduled.executionFailed"] = NcwTextKind.SchedErrFailed,
        ["scheduled.executionIncomplete"] = NcwTextKind.SchedErrFailed,
        ["scheduled.modelUnavailable"] = NcwTextKind.SchedErrModel,
        ["scheduled.workspaceUnavailable"] = NcwTextKind.SchedErrWorkspace,
        ["schedules.stopped"] = NcwTextKind.SchedErrStopped,
        // gateway
        ["gateway.portInUse"] = NcwTextKind.ErrGatewayPortInUse,
        ["gateway.cannotListen"] = NcwTextKind.ErrGatewayCannotListen,
        ["gateway.invalid.role"] = NcwTextKind.ErrInvalid,
        // usage
        ["usage.invalid.window"] = NcwTextKind.ErrInvalid,
        ["usage.invalid.status"] = NcwTextKind.ErrInvalid,
        // plugins(设置页的插件;稳定 key 来自 Ncw.Agent.Plugins 的 PluginErrorKeys)
        ["plugins.notFound"] = NcwTextKind.ErrPluginsNotFound,
        ["plugins.notLoaded"] = NcwTextKind.ErrPluginsNotFound,
        ["plugins.packageTooLarge"] = NcwTextKind.ErrPluginsPackageTooLarge,
        ["plugins.zipInvalid"] = NcwTextKind.ErrPluginsZipInvalid,
        ["plugins.packageFileMissing"] = NcwTextKind.ErrPluginsZipInvalid,
        ["plugins.manifestInvalid"] = NcwTextKind.ErrPluginsManifestInvalid,
        ["plugins.engineIncompatible"] = NcwTextKind.ErrPluginsEngineIncompatible,
        ["plugins.digestMismatch"] = NcwTextKind.ErrPluginsDigest,
        ["plugins.authRequired"] = NcwTextKind.ErrPluginsAuthRequired,
        ["plugins.scopeRequired"] = NcwTextKind.ErrPluginsScopeRequired,
        ["plugins.versionUnavailable"] = NcwTextKind.ErrPluginsVersionUnavailable,
        ["plugins.noVersion"] = NcwTextKind.ErrPluginsVersionUnavailable,
        ["plugins.marketFailed"] = NcwTextKind.ErrPluginsMarketFailed,
        ["plugins.diskPermission"] = NcwTextKind.ErrPluginsDisk,
        ["plugins.notRunnable"] = NcwTextKind.ErrPluginsNotRunnable,
        ["plugins.faulted"] = NcwTextKind.ErrPluginsFaulted,
        ["plugins.commandUndeclared"] = NcwTextKind.ErrPluginsCommandFailed,
        ["plugins.commandFailed"] = NcwTextKind.ErrPluginsCommandFailed,
        ["plugins.permissionOutsideManifest"] = NcwTextKind.ErrPluginsPermissionOutside,
        ["plugins.invalidArgument"] = NcwTextKind.ErrInvalid,
        ["plugins.interactionNotFound"] = NcwTextKind.ErrPluginsInteractionGone,
        ["plugins.configurationInvalid"] = NcwTextKind.ErrPluginsConfigInvalid,
        ["plugins.operationFailed"] = NcwTextKind.ErrPluginsGeneric,
        // account(登录 / 团队 / 令牌;稳定 key 来自 Ncw.Client.Accounts.AccountKeys)
        ["account.login.browserFailed"] = NcwTextKind.ErrAccountBrowserFailed,
        ["account.login.expired"] = NcwTextKind.ErrAccountLoginExpired,
        ["account.login.denied"] = NcwTextKind.ErrAccountDenied,
        ["account.login.failed"] = NcwTextKind.ErrAccountLoginFailed,
        ["account.login.exchangeFailed"] = NcwTextKind.ErrAccountExchangeFailed,
        ["account.login.network"] = NcwTextKind.ErrAccountNetwork,
        ["account.login.noTeam"] = NcwTextKind.ErrAccountNoTeam,
        ["account.login.deviceCodeUnavailable"] = NcwTextKind.ErrAccountDeviceCodeUnavailable,
        ["account.notSignedIn"] = NcwTextKind.ErrAccountNotSignedIn,
        ["account.sessionExpired"] = NcwTextKind.ErrAccountSessionExpired,
        ["account.unreachable"] = NcwTextKind.ErrAccountUnreachable,
        ["account.rejected"] = NcwTextKind.ErrAccountRejected,
        ["account.team.invalid"] = NcwTextKind.ErrAccountTeamInvalid,
        // environments(执行环境 / SSH;稳定 key 来自 Ncw.Protocol.Methods.EnvironmentErrorKeys)
        ["environments.notFound"] = NcwTextKind.ErrEnvNotFound,
        ["environments.invalidProfile"] = NcwTextKind.ErrEnvInvalidProfile,
        ["environments.conflict"] = NcwTextKind.ErrEnvConflict,
        ["environments.inUse"] = NcwTextKind.ErrEnvInUse,
        ["environments.busy"] = NcwTextKind.ErrEnvBusy,
        ["environments.disabled"] = NcwTextKind.ErrEnvDisabled,
        ["environments.notConnected"] = NcwTextKind.ErrEnvNotConnected,
        ["environments.localImmutable"] = NcwTextKind.ErrEnvLocalImmutable,
        ["environments.notRemote"] = NcwTextKind.ErrEnvNotRemote,
        ["environments.targetChangeNeedsConfirm"] = NcwTextKind.ErrEnvTargetChangeNeedsConfirm,
        ["environments.secretsUnavailable"] = NcwTextKind.ErrEnvSecretsUnavailable,
        ["environments.noSshServices"] = NcwTextKind.ErrEnvNoSshServices,
        ["environments.authentication"] = NcwTextKind.ErrEnvAuthentication,
        ["environments.hostKeyChanged"] = NcwTextKind.ErrEnvHostKeyChanged,
        ["environments.hostKeyRejected"] = NcwTextKind.ErrEnvHostKeyRejected,
        ["environments.cancelled"] = NcwTextKind.ErrEnvCancelled,
        ["environments.connectionFailed"] = NcwTextKind.ErrEnvConnectionFailed,
        ["environments.timeout"] = NcwTextKind.ErrEnvTimeout,
        ["environments.disconnected"] = NcwTextKind.ErrEnvDisconnected,
        ["environments.unsupportedPlatform"] = NcwTextKind.ErrEnvUnsupportedPlatform,
        ["environments.unsupported"] = NcwTextKind.ErrEnvUnsupported,
        ["environments.invalidPath"] = NcwTextKind.ErrEnvInvalidPath,
        ["environments.pathNotFound"] = NcwTextKind.ErrEnvPathNotFound,
        ["environments.permissionDenied"] = NcwTextKind.ErrEnvPermissionDenied,
        ["environments.internal"] = NcwTextKind.ErrEnvGeneric,
    };

    private static readonly (string Prefix, NcwTextKind Kind)[] ErrorPrefixes =
    [
        ("mcp.connect.", NcwTextKind.ErrMcpConnGeneric),
        ("mcp.tool.", NcwTextKind.ErrMcpToolRejected),
        ("storage.backup.", NcwTextKind.ErrStorageBackupFile),
        ("storage.import.", NcwTextKind.ErrStorageImportFile),
        ("storage.transfer.", NcwTextKind.ErrStorageImportFile),
        ("plugins.", NcwTextKind.ErrPluginsGeneric),
        ("skills.", NcwTextKind.ErrSkillsGeneric),
        ("hooks.", NcwTextKind.ErrHooksGeneric),
        ("hooks.diagnostic.", NcwTextKind.ErrHooksGeneric),
        ("environments.", NcwTextKind.ErrEnvGeneric),
    ];
}
