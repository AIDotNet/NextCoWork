using System.Buffers.Binary;
using System.Text;
using Ncw.Agent.Host.Conversation;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Environments;

/// <summary>
/// 路由前置闸门(<see cref="MethodRouter.BeforeInvoke"/>):凡是要碰项目的执行环境的方法(文件 / git / 终端 / Skills / 命令 / Hooks / 子代理 / 插件命令),
/// 参数里指向 SSH 项目时先确保它的环境已连接——这样 SSH 项目上这些方法「自然可用」,方法实现里仍然只拿同步的 <c>EnvironmentOf</c>,不各自处理连接。
/// <list type="bullet">
/// <item><b>惰性连接,首次指纹 / 凭据以交互出现</b>(任何控制端可答),请求在这里等用户回答;请求被取消(控制端断开、<c>cancel</c> 帧)时一并取消。</item>
/// <item><b>只有 member 起才会发起连接</b>:viewer 是只读访客,不能让设备去连内网主机、也不能替用户在别的控制端上弹出确认框——环境没连着就是可重试的 <c>unavailable</c>(<c>environments.notConnected</c>)。</item>
/// <item>本机项目、没有项目 id 的调用、项目不存在(留给方法实现报 <c>not_found</c>)都直接放行。</item>
/// <item><b>请求期间持有环境的租约</b>:方法实现跑多久,空闲回收器就等多久。没有它的话,一次比空闲阈值更久的操作(SSH 上的 <c>git fetch</c>、读大文件、
/// 建终端)会在中途被回收器断开——「确保已连接」只是把最后使用时间记到请求开始的那一刻。</item>
/// </list>
/// 项目 id 从 MessagePack 参数里按键名取(<c>WorkspaceId</c> / <c>ProjectId</c>):参数形状只有协议层一份定义,这里不重复一份类型表。
/// 按会话 id 进来的方法(<c>runs.start</c> 等)在各自的处理器里 <c>await EnvironmentOfAsync</c>。
/// </summary>
internal static class ProjectEnvironmentGate
{
    /// <summary>需要项目执行环境的方法名前缀 / 全名(参数里有项目 id 才起作用)。</summary>
    private static readonly string[] Prefixes = ["files.", "git."];

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "terminals.prepare", "terminals.create",
        "skills.list", "skills.diagnostics", "skills.installZip", "skills.installMarket", "skills.uninstall",
        "commands.list", "commands.listAll", "commands.diagnostics",
        "hooks.list", "hooks.diagnostics", "hooks.save", "hooks.delete", "hooks.setEnabled", "hooks.test",
        "subagents.list", "subagents.diagnostics",
        "plugins.runCommand",
    };

    public static void Install(MethodRouter router, Func<ConversationService?> conversation) =>
        router.BeforeInvoke = (def, request, context, ct) => InvokeAsync(conversation(), def, request, context, ct);

    internal static bool NeedsEnvironment(string method) => Methods.Contains(method) || Prefixes.Any(p => method.StartsWith(p, StringComparison.Ordinal));

    private static async ValueTask<IDisposable?> InvokeAsync(ConversationService? conversation, MethodDef def, RequestFrame request, CallContext context, CancellationToken ct)
    {
        if (conversation is null || !NeedsEnvironment(def.Name))
        {
            return null;
        }

        if (FindProjectId(request.Params) is not { Length: > 0 } projectId || conversation.Workspaces.Get(projectId) is not { EnvironmentKind: "connection" } project)
        {
            return null;
        }

        // 先取租约再确保连接:中间不留「已连接、但还没人占着」的空档
        var lease = conversation.Environments.Hold(project.ConnectionId);
        try
        {
            await conversation.Environments.EnsureProjectReadyAsync(project, context.Role >= Role.Member ? ConnectMode.Interactive : ConnectMode.None, ct).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>从 MessagePack 编码的参数(字符串键的 map)里取 <c>WorkspaceId</c> / <c>ProjectId</c> 的字符串值;没有、不是字符串、格式不对返回 null(交给方法实现去报错)。</summary>
    internal static string? FindProjectId(ReadOnlySpan<byte> payload)
    {
        try
        {
            var reader = new Scanner(payload);
            var entries = reader.MapHeader();
            for (var i = 0; i < entries; i++)
            {
                var key = reader.TryString(out var keyText) ? keyText : null;
                if (key is "WorkspaceId" or "ProjectId")
                {
                    return reader.TryString(out var value) ? value : null;
                }

                reader.Skip(0);
            }
        }
        catch (FormatException)
        {
        }

        return null;
    }

    /// <summary>够用的 MessagePack 扫描器:只读 map 头、字符串,并能跳过任意值(有深度上限,越界一律 <see cref="FormatException"/>)。</summary>
    private ref struct Scanner(ReadOnlySpan<byte> data)
    {
        private const int MaxDepth = 32;
        private ReadOnlySpan<byte> _data = data;

        public int MapHeader()
        {
            var b = Byte();
            if ((b & 0xF0) == 0x80)
            {
                return b & 0x0F;
            }

            return b switch
            {
                0xDE => (int)UInt(2),
                0xDF => (int)Math.Min(UInt(4), int.MaxValue),
                _ => throw new FormatException("not a map"),
            };
        }

        public bool TryString(out string value)
        {
            var b = Peek();
            int length;
            if ((b & 0xE0) == 0xA0)
            {
                _data = _data[1..];
                length = b & 0x1F;
            }
            else if (b is 0xD9 or 0xDA or 0xDB)
            {
                _data = _data[1..];
                length = (int)Math.Min(UInt(b == 0xD9 ? 1 : b == 0xDA ? 2 : 4), int.MaxValue);
            }
            else
            {
                value = "";
                Skip(0);
                return false;
            }

            if (length > _data.Length)
            {
                throw new FormatException("truncated");
            }

            value = Encoding.UTF8.GetString(_data[..length]);
            _data = _data[length..];
            return true;
        }

        public void Skip(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("too deep");
            }

            var b = Byte();
            switch (b)
            {
                case <= 0x7F or >= 0xE0 or 0xC0 or 0xC2 or 0xC3:
                    return;
                case >= 0x80 and <= 0x8F:
                    SkipItems((b & 0x0F) * 2, depth);
                    return;
                case >= 0x90 and <= 0x9F:
                    SkipItems(b & 0x0F, depth);
                    return;
                case >= 0xA0 and <= 0xBF:
                    SkipBytes(b & 0x1F);
                    return;
            }

            switch (b)
            {
                case 0xCC or 0xD0: SkipBytes(1); return;
                case 0xCD or 0xD1: SkipBytes(2); return;
                case 0xCE or 0xD2 or 0xCA: SkipBytes(4); return;
                case 0xCF or 0xD3 or 0xCB: SkipBytes(8); return;
                case 0xD9 or 0xC4: SkipBytes(UInt(1)); return;
                case 0xDA or 0xC5: SkipBytes(UInt(2)); return;
                case 0xDB or 0xC6: SkipBytes(UInt(4)); return;
                case 0xDC: SkipItems(UInt(2), depth); return;
                case 0xDD: SkipItems(UInt(4), depth); return;
                case 0xDE: SkipItems(UInt(2) * 2, depth); return;
                case 0xDF: SkipItems(UInt(4) * 2, depth); return;
                case 0xD4: SkipBytes(2); return;
                case 0xD5: SkipBytes(3); return;
                case 0xD6: SkipBytes(5); return;
                case 0xD7: SkipBytes(9); return;
                case 0xD8: SkipBytes(17); return;
                case 0xC7: SkipBytes(UInt(1) + 1); return;
                case 0xC8: SkipBytes(UInt(2) + 1); return;
                case 0xC9: SkipBytes(UInt(4) + 1); return;
                default: throw new FormatException("unknown type");
            }
        }

        private void SkipItems(long count, int depth)
        {
            for (long i = 0; i < count; i++)
            {
                Skip(depth + 1);
            }
        }

        private void SkipBytes(long count)
        {
            if (count > _data.Length)
            {
                throw new FormatException("truncated");
            }

            _data = _data[(int)count..];
        }

        private byte Peek() => _data.Length > 0 ? _data[0] : throw new FormatException("truncated");

        private byte Byte()
        {
            var b = Peek();
            _data = _data[1..];
            return b;
        }

        private uint UInt(int width)
        {
            if (_data.Length < width)
            {
                throw new FormatException("truncated");
            }

            var value = width switch
            {
                1 => _data[0],
                2 => BinaryPrimitives.ReadUInt16BigEndian(_data),
                _ => BinaryPrimitives.ReadUInt32BigEndian(_data),
            };
            _data = _data[width..];
            return value;
        }
    }
}
