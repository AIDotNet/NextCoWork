using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Llm;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Workspace.Git;

namespace Ncw.Agent.Host.Workspace;

internal sealed partial class WorkspaceToolsRuntime
{
    private Func<string, ICommitMessageModel> _commitMessages = static _ => throw new InvalidOperationException("not initialised");

    partial void InitGit()
    {
        // 模型解析同源:注入的(测试)→ 否则供应商运行时(和对话链路拿到的是同一个 IModelResolver)
        IModelResolver models = Services.Models ?? Providers;
        _commitMessages = Services.CommitMessages ?? (workspaceId => new HostCommitMessageModel(models, () => Conversation.DeviceSettings.Current().DefaultModel, workspaceId, Clock));
    }

    /// <summary>项目的 Git 服务(每个项目一份,很便宜:不持有任何连接)。项目不存在 <c>not_found</c>;环境暂时不可用 <c>unavailable</c>。</summary>
    public GitService GitOf(string workspaceId)
    {
        var project = Project(workspaceId);
        // 已保存的 Git 主机凭据按需现取,经环境变量交给 git(见 GitCredentialVault);没有凭据库(数据没就绪)时不注入
        return new GitService(project.Environment, project.Root, GitCredentials is { } vault ? new GitServiceOptions { ExtraEnvironment = vault.Environment } : null);
    }

    /// <summary>Git 主机凭据库(Host 在数据就绪后装配;<c>git pull / push</c> 和克隆用它的令牌)。</summary>
    internal GitCredentialVault? GitCredentials { get; set; }

    /// <summary>
    /// 这个项目的「写提交信息」模型。<b>模型取自设置里的默认模型</b>(2.x 的 <c>settings.defaultModel</c>),面板上不给选择器:
    /// 用户点的是「帮我写一句话」,不是「我要挑一个模型」。代价是没配默认模型时这颗按钮会失败,所以单给了一个 <c>git.aiNoModel</c> 而不是含糊的「生成失败」。
    /// </summary>
    public ICommitMessageModel CommitMessageModelFor(string workspaceId) => _commitMessages(workspaceId);
}

/// <summary>
/// <see cref="ICommitMessageModel"/> 的 Host 实现:一次<b>无工具</b>的旁路模型请求(形状照抄目标判定 / 压缩的旁路调用)。
/// 跟着设置里的默认模型走同一家供应商(漂到另一家是钱包问题);能关思考就关、关不掉取最低一档(<see cref="CompactBinding.AuxiliaryThinking"/>,压缩 / 权限审核 / 目标判定共用的规则);
/// 最大输出 ≤ 1024;<b>独立的 runId</b>(<c>commitmsg_&lt;ulid&gt;</c>):路由按 runId 记用量,写提交信息花的 token 不混进任何一次对话的合计。超时由调用方(<see cref="CommitMessageGenerator"/>)用取消令牌实现。
/// </summary>
internal sealed class HostCommitMessageModel(IModelResolver models, Func<Ncw.Agent.Services.Settings.ModelPick> pick, string workspaceId, TimeProvider clock) : ICommitMessageModel
{
    private const int MaxOutputTokens = 1024;

    public async Task<CommitMessageCompletion> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var chosen = pick();
        if (!chosen.IsSet)
        {
            throw new CommitMessageModelException(GitKeys.AiNoModel);
        }

        var model = new ModelRef(chosen.ProviderId, chosen.Model);
        var binding = models.Resolve(model) ?? throw new CommitMessageModelException(GitKeys.AiNoModel);
        var maxOutput = binding.MaxOutputTokens > 0 ? Math.Min(MaxOutputTokens, binding.MaxOutputTokens) : MaxOutputTokens;
        var runId = $"commitmsg_{Ncw.Core.Ids.Ulid.New(clock)}";
        var message = new Message(new MessageId($"{runId}:input"), Role.User, [new TextPart(user)], clock.GetUtcNow());
        var request = new LlmRequest(system, [message], [], new GenerationOptions(maxOutput, CompactBinding.AuxiliaryThinking(ThinkingLevel.Off, binding)));

        var text = new System.Text.StringBuilder();
        var complete = false;
        await foreach (var ev in models.Client.StreamAsync(model, request, new RequestContext(workspaceId, runId), ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case TextDelta delta:
                    text.Append(delta.Text);
                    break;
                case MessageEnd end:
                    complete = end.Stop is StopReason.EndTurn or StopReason.StopSequence;
                    break;
                case StreamError error:
                    // 上游原文(URL、模型 ID、供应商错误码)只进诊断,界面上给一句人话
                    throw new CommitMessageModelException(GitKeys.AiFailed, error.Error.Code.ToString());
            }

            if (text.Length > CommitMessageGenerator.ResponseLimit)
            {
                break;
            }
        }

        return new CommitMessageCompletion(text.ToString(), complete);
    }
}
