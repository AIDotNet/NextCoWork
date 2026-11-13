namespace Ncw.Agent.Host;

/// <summary>Agent 的运行形态(docs/design/02 A1):桌面内置或云端。3.0 不做独立设备服务。</summary>
public enum AgentMode
{
    Local,
    Cloud,
}

/// <summary>启动参数。只给桌面端和云端编排调用,不是面向用户的命令行(决策:3.0 只做 GUI)。</summary>
/// <param name="DataRoot">数据根。M2 阶段尚未实现 2.x 数据接管,桌面端传入独立的开发数据根,不碰 2.x 的 ~/.next-cowork/data。</param>
/// <param name="Endpoint">本机承载的端点(socket 路径 / 管道名)。</param>
/// <param name="LocalToken">本机令牌:桌面端拉起时通过环境变量传入,hello 里核对。</param>
/// <param name="ParentPid">父进程(桌面端)。父进程退出后 Agent 跟着退出,避免留下孤儿进程。</param>
public sealed record AgentOptions(AgentMode Mode, string DataRoot, string Endpoint, string LocalToken, int? ParentPid);
