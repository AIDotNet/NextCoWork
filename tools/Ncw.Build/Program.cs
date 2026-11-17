using Ncw.Build.Bundle;
using Ncw.Build.Cli;
using Ncw.Build.Core;
using Ncw.Build.Publish;
using Ncw.Build.Release;
using Ncw.Build.Signing;
using Ncw.Build.Velopack;
using Ncw.Build.Verify;

namespace Ncw.Build;

internal static class Program
{
    /// <summary>子命令表。顺序就是一条完整发布流水线的顺序。</summary>
    internal static IReadOnlyList<IBuildCommand> Commands { get; } =
    [
        new PublishAgentCommand(),
        new PublishDesktopCommand(),
        new BundleCommand(),
        new VerifyCommand(),
        new SignCommand(),
        new VelopackCommand(),
        new ManifestCommand(),
        new ManifestVerifyCommand(),
        new KeygenCommand(),
        new UploadCommand(),
        // 服务器中继的发布与自检,独立于上面「桌面应用」的那条流水线(中继是服务端部署物)。
        new BuildRelayCommand(),
    ];

    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return await RunAsync(args, null, cts.Token).ConfigureAwait(false);
    }

    /// <summary>测试入口:可以换掉运行环境。<paramref name="context"/> 为 null 时按真实环境创建。</summary>
    internal static async Task<int> RunAsync(string[] args, BuildContext? context, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(Console.Out);
            return args.Length == 0 ? 2 : 0;
        }

        var command = Commands.FirstOrDefault(c => c.Spec.Name == args[0]);
        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintHelp(Console.Error);
            return 2;
        }

        if (args.Skip(1).Any(a => a is "-h" or "--help"))
        {
            PrintCommandHelp(command.Spec, Console.Out);
            return 0;
        }

        IBuildLog? log = context?.Log;
        try
        {
            var parsed = ParsedArgs.Parse(command.Spec, args.Skip(1).ToArray());
            var ctx = context ?? BuildContext.Create(RepoLayout.Find(parsed.Get("artifacts")));
            log = ctx.Log;
            return await command.RunAsync(ctx, parsed, ct).ConfigureAwait(false);
        }
        catch (BuildException e)
        {
            (log ?? new ConsoleBuildLog()).Error(e.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            (log ?? new ConsoleBuildLog()).Error("cancelled");
            return 130;
        }
    }

    private static void PrintHelp(TextWriter writer)
    {
        writer.WriteLine("Ncw.Build — NextCoWork build and release pipeline (developers and CI only; not shipped to users).");
        writer.WriteLine();
        writer.WriteLine("Usage: dotnet run --project tools/Ncw.Build -- <command> [options]");
        writer.WriteLine();
        foreach (var c in Commands)
        {
            writer.WriteLine($"  {c.Spec.Name,-16} {c.Spec.Summary}");
        }

        writer.WriteLine();
        writer.WriteLine("Use '<command> --help' for the options of one command.");
    }

    private static void PrintCommandHelp(CommandSpec spec, TextWriter writer)
    {
        writer.WriteLine(spec.Summary);
        writer.WriteLine();
        writer.WriteLine("Usage: " + spec.Usage);
        foreach (var example in spec.Examples)
        {
            writer.WriteLine("  e.g. " + example);
        }
    }
}
