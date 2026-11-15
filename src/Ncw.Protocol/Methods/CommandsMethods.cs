using PolyType;

namespace Ncw.Protocol.Methods;

public static partial class Capabilities
{
    /// <summary>斜杠命令:<c>commands.*</c>(清单 / 诊断 / 全部 / 启停)。读取 viewer 起,启停 owner。</summary>
    public const string Commands = "commands";
}

/// <summary>
/// 斜杠命令的方法表(docs/design/09 §8.4,功能盘点 10 的 <c>commands</c> 模块)。命令是<b>一段存在磁盘上的提示词模板</b>,不是一个能执行的动作:
/// <b>展开在客户端发送前完成</b>(<c>Ncw.Core.Domain.CommandExpansion</c>:<c>$ARGUMENTS</c>、<c>$1–$9</c>),设备收到的是普通文本,所以手机和桌面看到的输入一致,命令不进 <c>RunRequest</c>。
/// <list type="bullet">
/// <item><c>commands.list</c>:<b>运行期</b>用的清单(已滤掉被关的,带正文,客户端据此展开);<c>commands.listAll</c>:管理界面用的清单(被关的也列出来,标 <c>Enabled</c>,不带正文)。</item>
/// <item><c>commands.setEnabled</c>:设备级偏好(存「被关掉的那些」,每机器偏好,不写进文件),owner;返回变更之后的 <c>listAll</c>。</item>
/// <item>清单 = 内置(<c>/init</c>)+ 一方扩展经 <c>RegisterCommand</c> 贡献的 + 全局目录 + 项目目录(后者覆盖前者的同名)。</item>
/// </list>
/// </summary>
public static class CommandsMethods
{
    public static readonly MethodDef<CommandsScopeParams, CommandsListResult> List =
        Query<CommandsScopeParams, CommandsListResult>("commands.list", Role.Viewer);

    public static readonly MethodDef<CommandsScopeParams, CommandsDiagnosticsResult> Diagnostics =
        Query<CommandsScopeParams, CommandsDiagnosticsResult>("commands.diagnostics", Role.Viewer);

    public static readonly MethodDef<CommandsScopeParams, CommandsListAllResult> ListAll =
        Query<CommandsScopeParams, CommandsListAllResult>("commands.listAll", Role.Viewer);

    /// <summary><see cref="CommandsSetEnabledParams.WorkspaceId"/> 只用来算返回的清单。</summary>
    public static readonly MethodDef<CommandsSetEnabledParams, CommandsListAllResult> SetEnabled =
        Command<CommandsSetEnabledParams, CommandsListAllResult>("commands.setEnabled", Role.Owner);

    public static readonly IReadOnlyList<MethodDef> All = [List, Diagnostics, ListAll, SetEnabled];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Commands };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Commands };
}

public static class CommandScopes
{
    public const string Builtin = "builtin";
    public const string Global = "global";
    public const string Project = "project";

    /// <summary>一方扩展经 <c>RegisterCommand</c> 贡献的。</summary>
    public const string Extension = "extension";
}

/// <summary><see cref="WorkspaceId"/> 为 null = 只看全局层 + 内置 + 扩展。</summary>
public sealed record CommandsScopeParams(string? WorkspaceId = null);

/// <summary>运行期清单里的一条:<see cref="Prompt"/> 是模板正文(展开后真正发给模型的那段话),<see cref="Source"/> 是磁盘路径(内置 / 扩展为空串)。</summary>
public sealed record CommandDto(string Name, string Description, string Prompt, string Scope, string Source, string? ArgumentHint);

public sealed record CommandsListResult(CommandDto[] Commands);

/// <summary>管理清单里的一行(不带正文)。</summary>
public sealed record CommandItemDto(string Name, string Description, string Scope, string Source, string? ArgumentHint, bool Enabled);

public sealed record CommandsListAllResult(CommandItemDto[] Commands);

/// <summary>一条加载诊断(稳定的 <c>commands.*</c> key + 英文消息 + 结构化参数)。</summary>
public sealed record CommandDiagnosticDto(string Path, string Key, string Message, Dictionary<string, string>? Params = null);

public sealed record CommandsDiagnosticsResult(CommandDiagnosticDto[] Diagnostics);

public sealed record CommandsSetEnabledParams(string Name, bool Enabled, string? WorkspaceId = null);

[GenerateShapeFor<CommandsScopeParams>]
[GenerateShapeFor<CommandsListResult>]
[GenerateShapeFor<CommandsListAllResult>]
[GenerateShapeFor<CommandsDiagnosticsResult>]
[GenerateShapeFor<CommandsSetEnabledParams>]
public sealed partial class PayloadShapes;
