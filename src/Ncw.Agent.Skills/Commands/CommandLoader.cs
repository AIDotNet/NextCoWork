using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Session;
using Ncw.Core.Domain;
using Ncw.Core.Text;

namespace Ncw.Agent.Skills.Commands;

/// <summary>
/// 斜杠命令的扫描(2.x <c>kernel/command/load.ts</c>)——结构照抄子代理定义的加载器(两层目录、同名时项目胜出、永不抛、一切失败变诊断),因为用户手写这两种文件时的心智是同一套。
/// 和子代理的两处差别:① <b>不要求 frontmatter</b>:命令的全部内容就是正文,<c>description</c> 只影响弹层里那一行副标题,缺了就用正文第一行兜底(子代理缺 description 必须作废,
/// 因为模型是靠它决定派不派活的);② <b>没有工具 / 权限字段</b>:展开后就是一段普通文本,不带任何运行期语义。
/// ★ <b>命令名只认文件名</b>,不认 frontmatter 里的 <c>name</c>:用户敲的是 <c>/文件名</c>,让文件里的一行能改掉这件事,等于让「目录里有什么」和「能敲出什么」分叉。
/// 连不上执行环境不是「没有命令」,原样抛出。
/// </summary>
public static class CommandLoader
{
    public static async Task<CommandScanResult> ScanAsync(CommandScanInput input, CancellationToken ct = default)
    {
        var diagnostics = new List<CommandDiagnostic>();
        var byName = new Dictionary<string, SlashCommand>(StringComparer.Ordinal);
        var order = new List<string>();

        void Put(SlashCommand command)
        {
            if (byName.TryAdd(command.Name, command))
            {
                order.Add(command.Name);
            }
            else
            {
                byName[command.Name] = command; // 同名:后进的覆盖先进的,位置不变
            }
        }

        // 内置先进(「至少有一条命令可用」无条件成立),然后是扩展贡献的,最后目录里的(全局先进,项目后进覆盖同名)
        foreach (var builtin in BuiltinCommands.All)
        {
            Put(builtin);
        }

        foreach (var extension in input.Extension ?? [])
        {
            if (CommandExpansion.IsValidName(extension.Name) && !byName.ContainsKey(extension.Name))
            {
                Put(new SlashCommand(
                    extension.Name,
                    PromptText.Clamp(PromptText.StripControlChars(extension.Description), CommandExpansion.DescriptionMax),
                    PromptText.Clamp(PromptText.StripControlChars(extension.Prompt).Trim(), CommandExpansion.PromptMax),
                    CommandScope.Extension, "", extension.ArgumentHint is null ? null : PromptText.StripControlChars(extension.ArgumentHint)));
            }
        }

        if (input.GlobalEnvironment is { } globalEnv && input.GlobalRoot is { } globalRoot)
        {
            await ScanRootAsync(globalEnv, globalRoot, CommandScope.Global, Put, () => byName.Count, byName, diagnostics, ct).ConfigureAwait(false);
        }

        if (input.ProjectEnvironment is { } projectEnv && input.ProjectRoot is { } projectRoot)
        {
            await ScanRootAsync(projectEnv, projectRoot, CommandScope.Project, Put, () => byName.Count, byName, diagnostics, ct).ConfigureAwait(false);
        }

        return new CommandScanResult([.. order.Select(n => byName[n])], diagnostics);
    }

    private static async Task ScanRootAsync(
        IExecutionEnvironment env, EnvPath root, CommandScope scope, Action<SlashCommand> put, Func<int> count, Dictionary<string, SlashCommand> byName,
        List<CommandDiagnostic> diagnostics, CancellationToken ct)
    {
        IReadOnlyList<FsDirEntry> entries;
        try
        {
            if (!await env.Fs.ExistsAsync(root, ct).ConfigureAwait(false))
            {
                return; // 没有 commands 目录是常态,不是错误
            }

            entries = await env.Fs.ListAsync(root, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (!SkillLoader.IsConnection(e))
        {
            diagnostics.Add(new CommandDiagnostic(root.Value, CommandDiagnosticKeys.DirectoryUnreadable, $"Could not read this directory: {e.Message}"));
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Info.IsDirectory || !entry.Name.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }

            var display = root.Join(entry.Name).Value;
            var stem = entry.Name[..^".md".Length];
            if (!CommandExpansion.IsValidName(stem))
            {
                diagnostics.Add(new CommandDiagnostic(display, CommandDiagnosticKeys.FileNameInvalid, "The file name is not a valid command name; skipped.", SkillLoader.Params(("file", entry.Name))));
                continue;
            }

            if (count() >= CommandLimits.MaxCommands && !byName.ContainsKey(stem))
            {
                diagnostics.Add(new CommandDiagnostic(root.Value, CommandDiagnosticKeys.TooMany, $"More than {CommandLimits.MaxCommands} commands; the rest were not loaded.", SkillLoader.Params(("max", CommandLimits.MaxCommands.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
                return;
            }

            EnvPath file;
            try
            {
                // 挡软链逃逸:commands/evil.md -> /etc/passwd 之后,选一次命令就等于把任意文件的内容塞进输入框发给模型
                file = await env.Paths.ResolveWithinAsync(root, entry.Name, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PathEscape)
            {
                diagnostics.Add(new CommandDiagnostic(display, CommandDiagnosticKeys.OutsideRoot, "This file resolves to somewhere outside the commands directory; skipped."));
                continue;
            }
            catch (EnvironmentException e) when (!SkillLoader.IsConnection(e))
            {
                diagnostics.Add(new CommandDiagnostic(display, CommandDiagnosticKeys.FileUnreadable, $"Could not read this file: {e.Message}"));
                continue;
            }

            if (await LoadOneAsync(env, file, stem, scope, diagnostics, ct).ConfigureAwait(false) is { } loaded)
            {
                put(loaded);
            }
        }
    }

    private static async Task<SlashCommand?> LoadOneAsync(
        IExecutionEnvironment env, EnvPath file, string name, CommandScope scope, List<CommandDiagnostic> diagnostics, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(await env.Fs.ReadHeadAsync(file, CommandLimits.FileMaxBytes, ct).ConfigureAwait(false));
        }
        catch (EnvironmentException e) when (!SkillLoader.IsConnection(e))
        {
            diagnostics.Add(new CommandDiagnostic(file.Value, CommandDiagnosticKeys.FileUnreadable, $"Could not read this file: {e.Message}"));
            return null;
        }

        var fm = FrontmatterParser.Parse(raw);
        foreach (var skipped in fm.Skipped)
        {
            diagnostics.Add(new CommandDiagnostic(file.Value, CommandDiagnosticKeys.Frontmatter, skipped));
        }

        var prompt = PromptText.Clamp(PromptText.StripControlChars(fm.Body).Trim(), CommandExpansion.PromptMax);
        if (prompt.Length == 0)
        {
            diagnostics.Add(new CommandDiagnostic(file.Value, CommandDiagnosticKeys.PromptEmpty, "The body (the prompt) is empty; this command was dropped."));
            return null;
        }

        var description = FrontmatterParser.String(fm, "description");
        var hint = FrontmatterParser.String(fm, "argument-hint") ?? FrontmatterParser.String(fm, "argumentHint");
        return new SlashCommand(
            name,
            PromptText.Clamp(PromptText.StripControlChars(description ?? FirstLine(prompt)), CommandExpansion.DescriptionMax),
            prompt, scope, file.Value, hint is null ? null : PromptText.StripControlChars(hint));
    }

    /// <summary>没写 description 时的兜底:拿正文第一行当摘要,总好过只显示一个命令名。</summary>
    private static string FirstLine(string prompt)
    {
        var line = prompt.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        return line.TrimStart('#').Trim();
    }
}
