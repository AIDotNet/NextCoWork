using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Cloud;

/// <summary><c>cloud.*</c>(方法表在 <see cref="CloudMethods"/>):只有 <c>--mode cloud</c> 的 Agent 注册,所以本机内置的 Agent 的 <c>welcome.features</c> 里没有 <c>cloud</c>。</summary>
internal static class CloudHandlers
{
    public static void Register(MethodRouter router, CloudRuntime runtime)
    {
        router.Handle(CloudMethods.GetStatus, (_, _, _) => ValueTask.FromResult(runtime.Status()));
        router.Handle(CloudMethods.ClearInterrupted, (p, _, _) =>
        {
            runtime.ClearInterrupted(p.RunIds ?? []);
            return ValueTask.FromResult(Unit.Value);
        });
    }
}
