using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>输入框不能发送的原因(没有原因 = <see cref="None"/>)。</summary>
public enum ComposerBlock
{
    None,

    /// <summary>没有打开的会话。</summary>
    NoSession,

    /// <summary>设备没连上(状态未知,规则 7)。</summary>
    DeviceUnavailable,

    /// <summary>本控制端在这台设备上是只读角色。</summary>
    ViewerRole,

    /// <summary>没有可用的模型(没配供应商)。</summary>
    NoModel,
}

/// <summary>输入框此刻能做什么。发送、排队、插话、停止是四个独立的意图,按钮的显隐都从这里来。</summary>
public readonly record struct ComposerState(bool CanSend, bool CanQueue, bool CanSteer, bool CanStop, ComposerBlock Block)
{
    /// <summary>主按钮:空闲时发送;运行中有文字排队、没文字停止。</summary>
    public ComposerPrimary Primary => CanSend ? ComposerPrimary.Send : CanQueue ? ComposerPrimary.Queue : CanStop ? ComposerPrimary.Stop : ComposerPrimary.Disabled;
}

public enum ComposerPrimary
{
    Disabled,
    Send,
    Queue,
    Stop,
}

/// <summary>
/// 输入框的状态机(纯函数,单测见 Ncw.Client.Tests)。输入:草稿、会话活动(Core 的 <see cref="SessionActivityView"/>)、设备连接、角色。
/// 设备断开时活动是 <see cref="SessionActivity.Unknown"/>:既不能发送也不能停止——命令发出去也到不了,状态未知不是「空闲」。
/// </summary>
public static class ComposerLogic
{
    /// <summary>权限档位(<see cref="SendOptionsDto.PermissionMode"/>)。<c>full</c> 只有 owner 能用。</summary>
    public static readonly IReadOnlyList<string> PermissionModes = ["ask", "auto", "full"];

    public static ComposerState Of(string draft, bool hasSession, SessionActivity activity, DeviceConnection connection, string? role, bool hasModel)
    {
        var block = !hasSession ? ComposerBlock.NoSession
            : connection != DeviceConnection.Connected || activity == SessionActivity.Unknown ? ComposerBlock.DeviceUnavailable
            : role == "viewer" ? ComposerBlock.ViewerRole
            : !hasModel ? ComposerBlock.NoModel
            : ComposerBlock.None;
        if (block != ComposerBlock.None)
        {
            return new ComposerState(false, false, false, false, block);
        }

        var hasText = !string.IsNullOrWhiteSpace(draft);
        var running = activity == SessionActivity.Running;
        return new ComposerState(
            CanSend: hasText && !running,
            CanQueue: hasText && running,
            CanSteer: hasText && running,
            CanStop: running,
            Block: ComposerBlock.None);
    }

    /// <summary>这台设备上这个角色能选的权限档位。</summary>
    public static IReadOnlyList<string> PermissionModesFor(string? role) =>
        role == "owner" ? PermissionModes : [.. PermissionModes.Where(m => m != "full")];

    /// <summary>当前档位对这个角色是否可用(角色降级、换设备后,不可用的档位退回 <c>ask</c>)。</summary>
    public static string ClampPermission(string mode, string? role) => PermissionModesFor(role).Contains(mode) ? mode : "ask";

    /// <summary>
    /// 这个模型可选的思考档位。不支持思考(<c>ThinkingMode</c> 为空)只有「关」;支持的给 <c>auto</c> + 模型声明的强度档位(没声明给通用的四档)。
    /// </summary>
    public static IReadOnlyList<string> ThinkingLevels(ModelOptionDto? model)
    {
        if (model is null)
        {
            return ["auto"];
        }

        if (model.ThinkingMode is null)
        {
            return ["off"];
        }

        IEnumerable<string> efforts = model.ReasoningEfforts is { Length: > 0 } declared ? declared.Where(e => e != "none") : ["low", "medium", "high"];
        return ["auto", "off", .. efforts];
    }

    /// <summary>当前思考档位对这个模型是否可用;不可用的退回第一个可用档位。</summary>
    public static string ClampThinking(string level, ModelOptionDto? model)
    {
        var levels = ThinkingLevels(model);
        return levels.Contains(level) ? level : levels[0];
    }

    /// <summary>
    /// 草稿里应当画成 token 的片段:开头的 <c>/命令</c>(是已知命令、后面跟着空白)和每个已引用文件的 <c>@名字</c>
    /// (前面是开头或空白,后面是结尾、空白或标点)。不重叠,按位置排序。输入框的 token 解析和「草稿里还有没有这个引用」都只看这一个函数。
    /// </summary>
    public static IReadOnlyList<MentionSpan> Mentions(string text, IEnumerable<FileRefChip> files, IEnumerable<string> commands)
    {
        var spans = new List<MentionSpan>();
        if (text.Length > 1 && text[0] == '/')
        {
            var end = 1;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            var name = text[1..end];
            if (end < text.Length && commands.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
            {
                spans.Add(new MentionSpan(MentionSpan.CommandKind, name, name, 0, end));
            }
        }

        // 长的先占位:「@a.cs」不会抢走「@a.cs.bak」的前半段
        foreach (var file in files.OrderByDescending(f => f.Mention.Length))
        {
            var mention = file.Mention;
            for (var at = text.IndexOf(mention, StringComparison.Ordinal); at >= 0; at = text.IndexOf(mention, at + 1, StringComparison.Ordinal))
            {
                var end = at + mention.Length;
                var startsWord = at == 0 || char.IsWhiteSpace(text[at - 1]);
                var endsWord = end == text.Length || char.IsWhiteSpace(text[end]) || char.IsPunctuation(text[end]) && text[end] is not ('.' or '_' or '-' or '/');
                if (startsWord && endsWord && spans.All(s => end <= s.Start || at >= s.End))
                {
                    spans.Add(new MentionSpan(MentionSpan.FileKind, file.Path, file.Label, at, end));
                }
            }
        }

        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    /// <summary>上下文窗口的简写:200K、1M。</summary>
    public static string WindowText(int window) => window switch
    {
        >= 1_000_000 when window % 1_000_000 == 0 => $"{window / 1_000_000}M",
        >= 1_000_000 => $"{window / 1_000_000.0:0.#}M",
        >= 1000 => $"{window / 1000}K",
        _ => window.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>草稿里画成 token 的一段(<see cref="ComposerLogic.Mentions"/>):种类、指向什么(文件路径 / 命令名)、符号后面的文字、范围(含符号,结尾不含)。</summary>
public sealed record MentionSpan(string Kind, string Id, string Label, int Start, int End)
{
    public const string FileKind = "file";
    public const string CommandKind = "command";
}
