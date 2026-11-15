using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Skills.Commands;
using Ncw.Core.Domain;

namespace Ncw.Agent.Skills.Tests;

/// <summary>
/// 斜杠命令:发现(两层目录、内置、扩展注册)、服务(启停 / 清单 / 诊断)。移植自 2.x <c>command-load.test.ts</c>(全局与项目分属两个执行环境、项目环境断线不能退回全局)、
/// <c>command-builtin.test.ts</c>(<c>/init</c>)。2.x 的 <c>device-commands.test.ts</c>(设备命令回执)与渲染层 <c>shell/commands.ts</c>(命令面板 / 快捷键)是别的东西,不属于斜杠命令,不移植。
/// </summary>
public sealed class CommandTests : IDisposable
{
    private readonly Fx _fx = new();

    public void Dispose() => _fx.Dispose();

    private string GlobalCommands => Path.Combine(_fx.Root, "global", "commands");

    private string ProjectCommands => Path.Combine(_fx.ProjectDir, ".next-cowork", "commands");

    private static void Put(string root, string file, string content)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, file), content);
    }

    private CommandScanInput Input(IExecutionEnvironment? projectEnvironment = null, IReadOnlyList<CommandDefinition>? extension = null) =>
        new(_fx.Env, _fx.P(GlobalCommands), projectEnvironment ?? _fx.Env, _fx.P(ProjectCommands), extension);

    private Task<CommandScanResult> ScanAsync(IReadOnlyList<CommandDefinition>? extension = null) => CommandLoader.ScanAsync(Input(extension: extension));

    // ───────────────────────── 内置 /init ─────────────────────────

    [Fact]
    public void Builtin_init_delegates_to_the_bundled_repo_init_skill()
    {
        var init = Assert.Single(BuiltinCommands.All, c => c.Name == "init");

        Assert.Contains("`repo-init`", init.Prompt);
        Assert.Equal("[update|agents-only] [--no-history]", init.ArgumentHint);
        Assert.Equal(CommandScope.Builtin, init.Scope);
    }

    [Fact]
    public void Init_passes_the_user_arguments_through_to_the_skill_and_stays_a_full_run_without_any()
    {
        var init = BuiltinCommands.All.Single(c => c.Name == "init");

        var withArgs = CommandExpansion.Expand(init.Prompt, "update --no-history");
        Assert.Contains("Arguments passed to /init (empty means a full run): update --no-history", withArgs);
        Assert.DoesNotContain("$ARGUMENTS", withArgs);

        var bare = CommandExpansion.Expand(init.Prompt, "");
        Assert.DoesNotContain("$ARGUMENTS", bare);
        Assert.Contains("Arguments passed to /init (empty means a full run): \n", bare);
    }

    [Fact]
    public void Init_stops_when_the_skill_is_missing_rather_than_writing_from_memory()
    {
        Assert.Contains("do not attempt the task from memory", BuiltinCommands.All.Single(c => c.Name == "init").Prompt);
    }

    // ───────────────────────── 发现 ─────────────────────────

    [Fact]
    public async Task Builtin_commands_are_always_present_even_with_no_directories()
    {
        var result = await CommandLoader.ScanAsync(new CommandScanInput(null, null, null, null));

        Assert.Equal(["init"], result.Commands.Select(c => c.Name));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Invariant_a_project_command_beats_a_global_one_of_the_same_name_and_the_two_layers_use_their_own_environments()
    {
        Put(GlobalCommands, "review.md", "Client review");
        Put(GlobalCommands, "client-only.md", "Client instructions");
        Put(ProjectCommands, "review.md", "Server review");

        var result = await ScanAsync();

        var review = result.Commands.Single(c => c.Name == "review");
        Assert.Equal(("Server review", CommandScope.Project), (review.Prompt, review.Scope));
        Assert.EndsWith(Path.Combine("commands", "review.md"), review.Source);
        Assert.Contains(Path.Combine(".next-cowork", "commands"), review.Source);
        var only = result.Commands.Single(c => c.Name == "client-only");
        Assert.Equal(("Client instructions", CommandScope.Global), (only.Prompt, only.Scope));
    }

    [Fact]
    public async Task Invariant_a_dropped_project_connection_does_not_substitute_the_global_commands()
    {
        Put(GlobalCommands, "review.md", "Client review");

        await Assert.ThrowsAsync<EnvironmentException>(() => CommandLoader.ScanAsync(Input(new DisconnectedEnvironment(_fx.Env))));
    }

    [Fact]
    public async Task A_command_does_not_need_any_frontmatter_and_the_first_line_stands_in_for_the_description()
    {
        Put(GlobalCommands, "plain.md", "\n# 解释这个文件\n\n逐行讲解。");

        var c = (await ScanAsync()).Commands.Single(c => c.Name == "plain");

        Assert.Equal(("解释这个文件", "# 解释这个文件\n\n逐行讲解。"), (c.Description, c.Prompt));
    }

    [Fact]
    public async Task Invariant_the_name_comes_only_from_the_file_name_never_from_frontmatter()
    {
        // 用户敲的是 /文件名:让文件里的一行能改掉这件事,等于让「目录里有什么」和「能敲出什么」分叉,而那个分叉没有任何提示。
        Put(GlobalCommands, "real.md", "---\nname: other\ndescription: d\nargument-hint: <file>\n---\nbody $ARGUMENTS");

        var result = await ScanAsync();

        Assert.DoesNotContain(result.Commands, c => c.Name == "other");
        var c = result.Commands.Single(c => c.Name == "real");
        Assert.Equal(("d", "<file>", "body $ARGUMENTS"), (c.Description, c.ArgumentHint, c.Prompt));
    }

    [Fact]
    public async Task The_camel_case_argument_hint_variant_is_accepted()
    {
        Put(GlobalCommands, "a.md", "---\nargumentHint: <x>\n---\nbody");

        Assert.Equal("<x>", (await ScanAsync()).Commands.Single(c => c.Name == "a").ArgumentHint);
    }

    [Fact]
    public async Task Bad_files_become_diagnostics_with_stable_keys_and_do_not_hide_the_good_ones()
    {
        Put(GlobalCommands, "good.md", "body");
        Put(GlobalCommands, "has space.md", "body");
        Put(GlobalCommands, "empty.md", "---\ndescription: d\n---\n   \n");
        Put(GlobalCommands, "notes.txt", "ignored silently");
        Put(GlobalCommands, "frontmatter.md", "---\ndescription: d\nnested:\n  a:\n    b: 1\n---\nbody");

        var result = await ScanAsync();

        Assert.Contains(result.Commands, c => c.Name == "good");
        Assert.Contains(result.Diagnostics, d => d.Key == CommandDiagnosticKeys.FileNameInvalid);
        Assert.Contains(result.Diagnostics, d => d.Key == CommandDiagnosticKeys.PromptEmpty);
        Assert.Contains(result.Diagnostics, d => d.Key == CommandDiagnosticKeys.Frontmatter);
        Assert.DoesNotContain(result.Commands, c => c.Name is "empty" or "notes");
        Assert.DoesNotContain(result.Diagnostics, d => d.Path.EndsWith("notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invariant_a_symlinked_command_file_pointing_outside_is_skipped()
    {
        // commands/evil.md -> /etc/passwd 之后,选一次命令就等于把任意文件的内容塞进输入框发给模型。
        var secret = Path.Combine(_fx.Root, "secret.md");
        File.WriteAllText(secret, "secret");
        Directory.CreateDirectory(GlobalCommands);
        File.CreateSymbolicLink(Path.Combine(GlobalCommands, "evil.md"), secret);

        var result = await ScanAsync();

        Assert.DoesNotContain(result.Commands, c => c.Name == "evil");
        Assert.Contains(result.Diagnostics, d => d.Key == CommandDiagnosticKeys.OutsideRoot);
    }

    [Fact]
    public async Task A_file_can_override_a_builtin_by_name_and_control_characters_are_stripped()
    {
        Put(GlobalCommands, "init.md", $"---\ndescription: mine{(char)27}\n---\nmy init");

        var init = (await ScanAsync()).Commands.Single(c => c.Name == "init");

        Assert.Equal(("my init", "mine", CommandScope.Global), (init.Prompt, init.Description, init.Scope));
    }

    [Fact]
    public async Task Extension_registered_commands_come_before_directories_so_a_users_file_always_wins()
    {
        var extension = new[]
        {
            new CommandDefinition("ext-only", "from extension", "ext prompt $1", "<a>"),
            new CommandDefinition("review", "ext review", "ext review"),
            new CommandDefinition("bad name", "x", "x"),
            new CommandDefinition("init", "tries to replace the builtin", "evil"),
        };
        Put(GlobalCommands, "review.md", "user review");

        var result = await ScanAsync(extension);

        Assert.Equal(("ext prompt $1", CommandScope.Extension, "<a>"), (result.Commands.Single(c => c.Name == "ext-only").Prompt, result.Commands.Single(c => c.Name == "ext-only").Scope, result.Commands.Single(c => c.Name == "ext-only").ArgumentHint));
        Assert.Equal("user review", result.Commands.Single(c => c.Name == "review").Prompt);
        Assert.Equal(CommandScope.Builtin, result.Commands.Single(c => c.Name == "init").Scope); // 扩展替换不了内置
        Assert.DoesNotContain(result.Commands, c => c.Name == "bad name");
    }

    [Fact]
    public async Task More_than_two_hundred_commands_stops_with_a_diagnostic()
    {
        for (var i = 0; i < CommandLimits.MaxCommands + 5; i++)
        {
            Put(GlobalCommands, $"c{i:D3}.md", "body");
        }

        var result = await ScanAsync();

        Assert.Equal(CommandLimits.MaxCommands, result.Commands.Count);
        Assert.Contains(result.Diagnostics, d => d.Key == CommandDiagnosticKeys.TooMany);
    }

    // ───────────────────────── 服务 ─────────────────────────

    private sealed class Locations(CommandTests t, IExecutionEnvironment? project = null) : ICommandLocations
    {
        public ValueTask<CommandScanInput> ScanInputAsync(string? workspaceId, CancellationToken ct) =>
            ValueTask.FromResult(workspaceId is null ? new CommandScanInput(t._fx.Env, t._fx.P(t.GlobalCommands), null, null) : t.Input(project));
    }

    [Fact]
    public async Task Invariant_the_runtime_list_drops_the_disabled_ones_while_the_management_list_keeps_them_marked()
    {
        // 管理界面必须列出关掉的那些,否则用户没法再打开它。
        Put(GlobalCommands, "a.md", "a");
        Put(GlobalCommands, "b.md", "b");
        var switches = new MemoryCommandSwitchStore();
        var service = new CommandsService(new Locations(this), switches);
        var changes = 0;
        service.Changed += () => changes++;

        service.SetEnabled("a", false);

        Assert.Equal(1, changes);
        Assert.Equal(["init", "b"], (await service.ListAsync("ws")).Select(c => c.Name).Order(StringComparer.Ordinal).Reverse().Reverse().OrderBy(n => n == "init" ? 0 : 1));
        Assert.DoesNotContain(await service.ListAsync("ws"), c => c.Name == "a");
        var all = (await service.ListAllAsync("ws")).ToDictionary(c => c.Name);
        Assert.False(all["a"].Enabled);
        Assert.True(all["b"].Enabled);

        service.SetEnabled("a", true);
        Assert.Contains(await service.ListAsync("ws"), c => c.Name == "a");
    }

    [Fact]
    public async Task A_builtin_can_be_switched_off_too()
    {
        var service = new CommandsService(new Locations(this), new MemoryCommandSwitchStore());

        service.SetEnabled("init", false);

        Assert.DoesNotContain(await service.ListAsync(null), c => c.Name == "init");
        Assert.False((await service.ListAllAsync(null)).Single(c => c.Name == "init").Enabled);
    }

    [Fact]
    public async Task The_service_rescans_every_time_and_includes_commands_registered_by_extensions()
    {
        var registered = new List<CommandDefinition>();
        var service = new CommandsService(new Locations(this), new MemoryCommandSwitchStore(), () => registered);
        Assert.DoesNotContain(await service.ListAsync("ws"), c => c.Name == "late");

        Put(GlobalCommands, "late.md", "late");
        registered.Add(new CommandDefinition("from-ext", "d", "p"));

        var names = (await service.ListAsync("ws")).Select(c => c.Name).ToList();
        Assert.Contains("late", names);
        Assert.Contains("from-ext", names);
    }

    [Fact]
    public async Task Diagnostics_pass_through()
    {
        Put(GlobalCommands, "has space.md", "x");

        var diagnostics = await new CommandsService(new Locations(this), new MemoryCommandSwitchStore()).DiagnosticsAsync("ws");

        Assert.Contains(diagnostics, d => d.Key == CommandDiagnosticKeys.FileNameInvalid);
    }

    // ───────────────────────── RegisterCommand 扩展点 ─────────────────────────

    private sealed class CommandExtension : IAgentExtension
    {
        public string Id => "cmd";

        public int Order => 0;

        public void Register(IExtensionRegistry registry) => registry.RegisterCommand(new CommandDefinition("hello", "say hi", "Say hi to $ARGUMENTS"));
    }

    [Fact]
    public void The_extension_host_collects_commands_registered_by_extensions()
    {
        var host = new ExtensionHost([new CommandExtension()]);

        var command = Assert.Single(host.RegisteredCommands);
        Assert.Equal(("hello", "Say hi to $ARGUMENTS"), (command.Name, command.Prompt));
    }
}
