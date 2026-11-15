namespace Ncw.Protocol.Methods;

// M4「工作区工具」领域的能力名(docs/design/09 §8.4、10 的 workspace / resource / git / review / terminal 模块)。
// 设备在 welcome.features 里列出自己已注册方法所属的能力;控制端据此决定能调哪些方法、订阅哪些主题,老设备缺哪个就降级。
public static partial class Capabilities
{
    /// <summary>项目文件:<c>files.*</c>(目录列表、读、写、变更、搜索、markdown 资源)。读取 viewer 起,写入 member 起。</summary>
    public const string Files = "files";

    /// <summary>Git:<c>git.*</c>(概览、分支、提交、diff、暂存、提交、拉取 / 推送、生成提交信息)。读取 viewer 起,写入 member 起。</summary>
    public const string Git = "git";

    /// <summary>改动审查:<c>review.*</c>(每个 Run 的变更集、文件 diff、撤销 / 重做)。读取 viewer 起,撤销 / 重做 member 起。</summary>
    public const string Review = "review";

    /// <summary>终端:<c>terminals.*</c> 和 <c>terminal/&lt;id&gt;</c> 主题。本机 PTY 不可用(Windows 10 1809 以前)时设备不声明这个能力。</summary>
    public const string Terminals = "terminals";
}
