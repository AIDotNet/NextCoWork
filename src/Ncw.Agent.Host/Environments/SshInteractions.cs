using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host.Environments;

/// <summary>
/// SSH 的交互(06 §1.4):主机指纹确认与凭据提问都经交互闸门,和审批同一套机制——任何已连接的控制端都能回答,先到先得,带执行环境标识(设备 + 环境),
/// 取消(连接请求被取消 / 控制端断开)时以中止清理。它们不属于任何 Run,所以每次提问开一个<b>设备级作用域</b>(<c>env:&lt;环境 id&gt;:&lt;ulid&gt;</c>),问完即关。
/// </summary>
internal sealed class SshInteractionBroker(InteractionGate gate, IIdGenerator ids)
{
    public async Task<InteractionResponse> AskAsync(string environmentId, InteractionRequest draft, CancellationToken ct)
    {
        var scope = gate.OpenRun(new RunId($"env:{environmentId}:{ids.NewId()}"));
        try
        {
            return await scope.RequestAsync(draft, ct).ConfigureAwait(false);
        }
        finally
        {
            scope.End(RunStatus.Done);
        }
    }
}

/// <summary>
/// 一个 SSH 环境(或一次试连接)的指纹确认 + 凭据提供者。
/// <list type="bullet">
/// <item><b>首次出现的指纹</b> → <see cref="SshHostKey"/> 交互;用户信任才返回 <c>true</c>(取消 / 中止 / 没人回答都是 <c>false</c>:没问到不能当成同意)。
/// 指纹变化只通知(<see cref="NotifyChangedAsync"/>:详情进环境状态),从不弹确认。</item>
/// <item><b>凭据</b>:密码 / 口令先看设备上保存的(NCK1,用户勾选过「记住」);没有、或上一次被服务器拒绝了,才发起 <see cref="SshCredential"/> 交互。
/// 键盘交互式不保存。「记住」只对已保存的环境有效。</item>
/// <item><b>非交互模式</b>(<see cref="EnvironmentService.NonInteractiveScope"/>,定时任务、没人在场):一律不问,取消。</item>
/// </list>
/// </summary>
internal sealed class SshCallbacks(
    SshInteractionBroker broker,
    ConnectionSecrets? secrets,
    string deviceId,
    string environmentId,
    string displayName,
    Func<string?> primaryHostName,
    Action<HostKeyMismatchDto> onMismatch,
    Func<bool> nonInteractive,
    string? draftPassword = null,
    string? draftPassphrase = null) : IHostKeyCallback, ISshCredentialProvider
{
    private ExecutionEnvironmentRef Ref => new(deviceId, environmentId, displayName, "ssh");

    public async ValueTask<bool> ConfirmNewHostAsync(HostKeyInfo presented, CancellationToken ct)
    {
        if (nonInteractive())
        {
            return false;
        }

        try
        {
            var response = await broker.AskAsync(environmentId, new SshHostKey(Ref, presented.HostName, presented.Algorithm, presented.FingerprintSha256), ct).ConfigureAwait(false);
            return response is SshHostKeyResponse { Trust: true };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 交互被中止(设备关闭、被清理)而不是连接被取消:没问到,按拒绝
            return false;
        }
    }

    public ValueTask NotifyChangedAsync(HostKeyInfo presented, IReadOnlyList<PinnedHostKey> pinned, CancellationToken ct)
    {
        onMismatch(new HostKeyMismatchDto(presented.HostName, presented.Algorithm, presented.FingerprintSha256, [.. pinned.Select(static p => new PinnedKeyDto(p.Algorithm, p.FingerprintSha256))]));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<string>?> AskAsync(SshCredentialRequest request, CancellationToken ct)
    {
        var savedRef = SavedRef(request);
        var hasSaved = false;
        if (!request.PreviousRejected)
        {
            // 试连接草稿里现填的凭据,或设备上保存过的:直接用,不打扰用户
            if (request.Kind == SshCredentialKind.Password && draftPassword is { Length: > 0 })
            {
                return [draftPassword];
            }

            if (request.Kind == SshCredentialKind.KeyPassphrase && draftPassphrase is { Length: > 0 })
            {
                return [draftPassphrase];
            }

            if (savedRef is not null && secrets is not null && Saved(secrets, savedRef) is { Length: > 0 } saved)
            {
                return [saved];
            }
        }
        else if (savedRef is not null && secrets is not null && Saved(secrets, savedRef) is not null)
        {
            // 已保存的被服务器拒绝了:作废它(别每次都先拿这个错的去试一遍),界面要把「已保存的不对」说出来
            hasSaved = true;
            secrets.Remove(savedRef);
        }

        if (nonInteractive())
        {
            return null;
        }

        var kind = request.Kind switch
        {
            SshCredentialKind.Password => SshCredentialPromptKind.Password,
            SshCredentialKind.KeyPassphrase => SshCredentialPromptKind.KeyPassphrase,
            _ => SshCredentialPromptKind.KeyboardInteractive,
        };
        var canRemember = secrets is not null && savedRef is not null;
        var draft = new SshCredential(
            Ref,
            kind,
            request.HostName,
            request.UserName,
            // 私钥路径不上线:只给文件名,让用户认得是哪一把
            KeyName: kind == SshCredentialPromptKind.KeyPassphrase && request.Detail is { Length: > 0 } path ? Path.GetFileName(path) : null,
            Instruction: kind == SshCredentialPromptKind.KeyboardInteractive ? request.Detail : null,
            Prompts: request.Prompts is null ? null : [.. request.Prompts.Select(static p => new SshCredentialPrompt(p.Text, p.Echo))],
            PreviousRejected: request.PreviousRejected,
            CanRemember: canRemember,
            HasSaved: hasSaved);

        SshCredentialResponse answer;
        try
        {
            answer = (SshCredentialResponse)await broker.AskAsync(environmentId, draft, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        if (answer.Answers is not { } values)
        {
            return null;
        }

        if (answer.Remember && canRemember && values.Count == 1 && secrets is not null)
        {
            try
            {
                secrets.Set(savedRef!, values[0]);
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or Ncw.Agent.Storage.StorageException or Microsoft.Data.Sqlite.SqliteException)
            {
                // 存不了(主密钥读不出来等):这次连接照常继续,只是下次还会问;不能让「记住」失败拖垮连接
            }
        }

        return values;
    }

    private string? SavedRef(SshCredentialRequest request) => secrets is null
        ? null
        : request.Kind switch
        {
            SshCredentialKind.Password => request.HostName == primaryHostName()
                ? ConnectionSecrets.PasswordRef(environmentId)
                : ConnectionSecrets.HopPasswordRef(environmentId, request.HostName),
            SshCredentialKind.KeyPassphrase when request.Detail is { Length: > 0 } path => ConnectionSecrets.PassphraseRef(environmentId, path),
            _ => null,
        };

    private static string? Saved(ConnectionSecrets secrets, string reference)
    {
        try
        {
            return secrets.Get(reference);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // 主密钥读不出来:当作没有保存的,问用户
        }
    }
}
