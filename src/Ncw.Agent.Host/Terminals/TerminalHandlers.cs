using Ncw.Agent.Host.Workspace;
using Ncw.Agent.Terminal;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Terminals;

/// <summary>
/// <c>terminals.*</c> 的方法实现和 <c>terminal/&lt;id&gt;</c> 主题(方法表在 <see cref="TerminalMethods"/>,06 §9.4)。角色、承载、幂等键由路由统一检查,这里只做业务:
/// 项目 → (环境, 根目录) → <see cref="TerminalManager"/> → DTO。失败一律映射成 <see cref="ProtocolException"/>(<see cref="TerminalMapping.Map"/>)。
/// 本机 PTY 不可用(<see cref="LocalTerminals.IsSupported"/> 为假:Windows 10 1809 以前)时不注册方法,设备就不声明 <c>terminals</c> 能力。
/// </summary>
internal static class TerminalHandlers
{
    public static void Register(MethodRouter router, TopicHub topics, Func<WorkspaceToolsRuntime> tools)
    {
        if (!LocalTerminals.IsSupported)
        {
            return;
        }

        topics.RegisterSource(new TerminalTopicSource(tools));

        router.Handle(TerminalMethods.List, (p, ctx, _) => Guard(() =>
        {
            var runtime = tools();
            runtime.Conversation.RequireProject(p.ProjectId);
            var controller = runtime.TerminalControllers.Track(ctx);
            return Task.FromResult(new TerminalsListResult([.. runtime.Terminals.List(p.ProjectId, controller).Select(TerminalMapping.ToDto)]));
        }));

        router.Handle(TerminalMethods.Prepare, (p, ctx, ct) => Guard(async () =>
        {
            var runtime = tools();
            var controller = runtime.TerminalControllers.Track(ctx);
            var result = await runtime.Terminals.PrepareAsync(Request(runtime, p.ProjectId, p.Columns, p.Rows, p.Id, p.Cwd, null), controller, ct).ConfigureAwait(false);
            return result switch
            {
                TerminalPreparation.Ready ready => new TerminalPrepareResult(Terminal: TerminalMapping.ToDto(ready.Terminal)),
                TerminalPreparation.ApprovalRequired approval => new TerminalPrepareResult(Intent: TerminalMapping.ToDto(approval.Intent)),
                _ => throw new InvalidOperationException("Unknown terminal preparation"),
            };
        }));

        router.Handle(TerminalMethods.Approve, (p, ctx, _) => Guard(() =>
        {
            var runtime = tools();
            var controller = runtime.TerminalControllers.Track(ctx);
            return Task.FromResult(new TerminalApproveResult(runtime.Terminals.Approve(p.IntentId, p.Approved, controller)));
        }));

        router.Handle(TerminalMethods.Create, (p, ctx, ct) => Guard(async () =>
        {
            var runtime = tools();
            var controller = runtime.TerminalControllers.Track(ctx);
            var info = await runtime.Terminals.CreateAsync(Request(runtime, p.ProjectId, p.Columns, p.Rows, p.Id, p.Cwd, p.Approval), controller, ct).ConfigureAwait(false);
            return TerminalMapping.ToDto(info);
        }));

        router.Handle(TerminalMethods.Close, (p, ctx, ct) => Guard(async () =>
        {
            var runtime = tools();
            await runtime.Terminals.CloseAsync(p.Id, runtime.TerminalControllers.Track(ctx), ct).ConfigureAwait(false);
            return Unit.Value;
        }));

        router.Handle(TerminalMethods.GetBuffer, (p, ctx, _) => Guard(() =>
        {
            var runtime = tools();
            var buffer = runtime.Terminals.GetBuffer(p.Id, runtime.TerminalControllers.Track(ctx));
            return Task.FromResult(new TerminalBufferDto(buffer.Id, buffer.Data.ToArray(), buffer.Truncated, buffer.Seq));
        }));

        // 租约闸(02 A2)尚未做:member 即可写,多个控制端同时写同一个终端不互斥。
        router.Handle(TerminalMethods.Write, (p, ctx, ct) => Guard(async () =>
        {
            var runtime = tools();
            await runtime.Terminals.WriteAsync(p.Id, p.Data, runtime.TerminalControllers.Track(ctx), ct).ConfigureAwait(false);
            return Unit.Value;
        }));

        router.Handle(TerminalMethods.Resize, (p, ctx, ct) => Guard(async () =>
        {
            var runtime = tools();
            await runtime.Terminals.ResizeAsync(p.Id, p.Columns, p.Rows, runtime.TerminalControllers.Track(ctx), ct).ConfigureAwait(false);
            return Unit.Value;
        }));

        router.Handle(TerminalMethods.Kill, (p, ctx, ct) => Guard(async () =>
        {
            var runtime = tools();
            await runtime.Terminals.KillAsync(p.Id, runtime.TerminalControllers.Track(ctx), ct).ConfigureAwait(false);
            return Unit.Value;
        }));
    }

    /// <summary>项目 → 创建请求。项目不存在 <c>not_found</c>;环境暂时不可用(SSH 断开)由 <c>tools().Project</c> 抛可重试的 <c>unavailable</c>。</summary>
    private static TerminalCreateRequest Request(WorkspaceToolsRuntime runtime, string projectId, int columns, int rows, string? id, string? cwd, string? approval)
    {
        var project = runtime.Project(projectId);
        return new TerminalCreateRequest(projectId, project.Environment, project.Root, columns, rows, id, cwd, approval);
    }

    private static async ValueTask<T> Guard<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception e) when (TerminalMapping.Map(e) is { } mapped)
        {
            throw mapped;
        }
    }
}
