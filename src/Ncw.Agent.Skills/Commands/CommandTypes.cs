using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Extensions;

namespace Ncw.Agent.Skills.Commands;

public enum CommandScope
{
    Builtin,
    Global,
    Project,

    /// <summary>一方扩展经 <c>RegisterCommand</c> 贡献的。</summary>
    Extension,
}

/// <summary>
/// 斜杠命令(<c>/命令</c>)——<b>一段存在磁盘上的提示词模板</b>,不是一个能执行的动作(2.x <c>shared/domain/command.ts</c>)。
/// <b>展开在客户端发送前完成,设备收到的是普通文本</b>(见 <c>Ncw.Core.Domain.CommandExpansion</c>):命令不进 <c>RunRequest</c>、不进内核。
/// </summary>
/// <param name="Source">磁盘路径;内置和扩展贡献的为空串。</param>
public sealed record SlashCommand(string Name, string Description, string Prompt, CommandScope Scope, string Source, string? ArgumentHint = null);

public static class CommandDiagnosticKeys
{
    public const string DirectoryUnreadable = "commands.directoryUnreadable";
    public const string FileNameInvalid = "commands.fileNameInvalid";
    public const string FileUnreadable = "commands.fileUnreadable";
    public const string OutsideRoot = "commands.outsideRoot";
    public const string Frontmatter = "commands.frontmatter";
    public const string PromptEmpty = "commands.promptEmpty";
    public const string TooMany = "commands.tooMany";
}

public sealed record CommandDiagnostic(string Path, string Key, string Message, IReadOnlyDictionary<string, string>? Params = null);

public sealed record CommandScanResult(IReadOnlyList<SlashCommand> Commands, IReadOnlyList<CommandDiagnostic> Diagnostics);

/// <summary>
/// 扫描的输入:全局目录(<c>&lt;数据根&gt;/commands</c>)在设备本机,项目目录(<c>&lt;项目&gt;/.next-cowork/commands</c>)在项目的执行环境里。根为 null = 跳过那一层。
/// <see cref="Extension"/> 是一方扩展 <c>RegisterCommand</c> 贡献的(先于目录里的进表:用户写的同名命令永远说了算)。
/// </summary>
public sealed record CommandScanInput(
    IExecutionEnvironment? GlobalEnvironment,
    EnvPath? GlobalRoot,
    IExecutionEnvironment? ProjectEnvironment,
    EnvPath? ProjectRoot,
    IReadOnlyList<CommandDefinition>? Extension = null)
{
    public const string CommandsDirectory = "commands";

    public static EnvPath ProjectCommandsRoot(EnvPath projectRoot) => projectRoot.Join(Ncw.Agent.Extensions.Permissions.ProjectRuleFile.DirectoryName, CommandsDirectory);

    public static EnvPath GlobalCommandsRoot(EnvPath dataRoot) => dataRoot.Join(CommandsDirectory);
}

public static class CommandLimits
{
    public const int FileMaxBytes = 128 * 1024;

    public const int MaxCommands = 200;
}
