using Ncw.Agent.Hooks;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Extensions;

/// <summary>
/// <c>hooks.*</c> 的方法实现(方法表在 <see cref="HooksMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:校验参数 → 调服务层 → 映射成 DTO。
/// 业务失败(<see cref="HookException"/>)转成协议错误:粗粒度的错误码给路由 / 通用处理,稳定的 <c>hooks.error.*</c> key + 参数放在 <c>details</c>(<c>ProviderErrorDto</c>)。
/// <b>环境变量的值只有请求里有</b>:DTO 映射(<see cref="ToDto"/>)只取变量名。设备数据没就绪时 <paramref name="service"/> 抛可重试的 <c>unavailable</c>。
/// </summary>
internal static class HooksHandlers
{
    public static void Register(MethodRouter router, Func<HooksService> service)
    {
        router.Handle(HooksMethods.List, (p, _, ct) => GuardAsync(async () =>
            new HooksListResult([.. (await service().ListAsync(p.WorkspaceId, ct).ConfigureAwait(false)).Select(ToDto)])));
        router.Handle(HooksMethods.Diagnostics, (p, _, ct) => GuardAsync(async () =>
            new HooksDiagnosticsResult([.. (await service().DiagnosticsAsync(p.WorkspaceId, ct).ConfigureAwait(false)).Select(ToDto)])));
        router.Handle(HooksMethods.Save, (p, _, ct) => GuardAsync(async () =>
        {
            var saved = await service().SaveAsync(new HookSaveRequest(ScopeOf(p.Scope), p.WorkspaceId, FromDto(p.Hook), EnvOf(p.Hook)), ct).ConfigureAwait(false);
            return new HooksSaveResult(ToDto(saved), HookValidation.Warn(saved.Hook));
        }));
        router.Handle(HooksMethods.Delete, (p, _, ct) => GuardAsync(async () =>
        {
            await service().DeleteAsync(ScopeOf(p.Scope), p.WorkspaceId, p.Id, ct).ConfigureAwait(false);
            return Unit.Value;
        }));
        router.Handle(HooksMethods.SetEnabled, (p, _, ct) => GuardAsync(async () =>
        {
            await service().SetEnabledAsync(ScopeOf(p.Scope), p.WorkspaceId, p.Id, p.Enabled, ct).ConfigureAwait(false);
            return Unit.Value;
        }));
        router.Handle(HooksMethods.Test, (p, _, ct) => GuardAsync(async () =>
            ToDto(await service().TestAsync(new HookTestRequest(ScopeOf(p.Scope), p.WorkspaceId, FromDto(p.Hook), EnvOf(p.Hook), string.IsNullOrEmpty(p.Hook.Id) ? null : p.Hook.Id), ct).ConfigureAwait(false))));
    }

    // ───────────────────────── 错误 ─────────────────────────

    private static async ValueTask<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (HookException e)
        {
            throw ToProtocol(e);
        }
    }

    private static ProtocolException ToProtocol(HookException e)
    {
        var (code, retryable) = e.Key switch
        {
            HookErrorKeys.NotFound or HookErrorKeys.NoWorkspace => (ErrorCodes.NotFound, false),
            HookErrorKeys.FileUnreadable => (ErrorCodes.Conflict, false),
            HookErrorKeys.WriteFailed => (ErrorCodes.Unavailable, true),
            _ => (ErrorCodes.InvalidRequest, false),
        };
        return ExtensionErrors.Coded(code, e.Message, e.Key, e.Params, retryable);
    }

    // ───────────────────────── 映射 ─────────────────────────

    private static HookScope ScopeOf(string scope) => scope switch
    {
        HookNames.ScopeGlobal => HookScope.Global,
        HookNames.ScopeProject => HookScope.Project,
        _ => throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown hook scope '{scope}'."),
    };

    private static string ScopeName(HookScope scope) => scope == HookScope.Global ? HookNames.ScopeGlobal : HookNames.ScopeProject;

    internal static HookDto ToDto(HookItem item) => new(
        item.Hook.Id,
        ScopeName(item.Scope),
        item.Hook.Event,
        item.Hook.Kind == HookKind.Prompt ? HookNames.TypePrompt : HookNames.TypeCommand,
        item.Hook.Matcher,
        item.Hook.Enabled,
        item.Hook.TimeoutMs,
        item.Hook.Description,
        item.Hook.Command,
        item.Hook.Prompt,
        item.Hook.Model,
        item.Hook.ModelProviderId,
        [.. item.Hook.Env.Keys.Order(StringComparer.Ordinal)], // 只有名字:值是密钥,不上线
        item.SourcePath);

    private static HookDiagnosticDto ToDto(HookDiagnostic d) =>
        new(d.Path, d.Key, d.Message, d.Params is null ? null : new Dictionary<string, string>(d.Params));

    private static HookRunReportDto ToDto(HookRunReport r) => new(
        r.HookId,
        ScopeName(r.Scope),
        r.ExitCode,
        r.Stdout,
        r.Stderr,
        r.DurationMs,
        r.Outcome switch
        {
            HookOutcome.Blocked => HookNames.OutcomeBlocked,
            HookOutcome.Timeout => HookNames.OutcomeTimeout,
            HookOutcome.SpawnFailed => HookNames.OutcomeSpawnFailed,
            _ => HookNames.OutcomeOk,
        },
        r.Decision switch
        {
            HookDecision.Allow => HookNames.DecisionAllow,
            HookDecision.Deny => HookNames.DecisionDeny,
            HookDecision.Ask => HookNames.DecisionAsk,
            _ => null,
        },
        r.Reason,
        r.AdditionalContext,
        r.Preview,
        r.PromptVerdict switch
        {
            PromptVerdict.Met => HookNames.VerdictMet,
            PromptVerdict.NotMet => HookNames.VerdictNotMet,
            PromptVerdict.Impossible => HookNames.VerdictImpossible,
            PromptVerdict.Skipped => HookNames.VerdictSkipped,
            _ => null,
        });

    private static HookDefinition FromDto(HookUpsertDto dto)
    {
        if (!HookEvents.IsValid(dto.Event))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown hook event '{dto.Event}'.");
        }

        var kind = dto.Type switch
        {
            HookNames.TypeCommand => HookKind.Command,
            HookNames.TypePrompt => HookKind.Prompt,
            _ => throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown hook type '{dto.Type}'."),
        };
        return new HookDefinition
        {
            Id = dto.Id ?? "",
            Event = dto.Event,
            Kind = kind,
            Matcher = dto.Matcher,
            Enabled = dto.Enabled,
            TimeoutMs = dto.TimeoutMs,
            Description = dto.Description,
            Command = dto.Command,
            Prompt = dto.Prompt,
            Model = dto.Model,
            ModelProviderId = dto.ModelProviderId,
        };
    }

    private static IReadOnlyList<HookEnvEntry>? EnvOf(HookUpsertDto dto) =>
        dto.Env is null ? null : [.. dto.Env.Select(e => new HookEnvEntry(e.Name, e.Value))];
}
