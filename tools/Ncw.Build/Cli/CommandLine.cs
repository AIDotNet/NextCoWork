using Ncw.Build.Core;

namespace Ncw.Build.Cli;

/// <summary>子命令声明:允许哪些带值的选项和开关。未知选项直接报错,拼错了不会被悄悄忽略。</summary>
internal sealed record CommandSpec(string Name, string Summary, string[] ValueOptions, string[] Flags, string[] Examples)
{
    public string Usage
    {
        get
        {
            var parts = ValueOptions.Select(o => $"[--{o} <value>]").Concat(Flags.Select(f => $"[--{f}]"));
            return $"{Name} {string.Join(' ', parts)}";
        }
    }
}

/// <summary>解析后的参数。选项写法:<c>--name value</c> 或 <c>--name=value</c>;同一个选项可以重复(取全部)。</summary>
internal sealed class ParsedArgs
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var list) ? list : [];

    public bool Has(string flag) => _flags.Contains(flag);

    public string Require(string name) =>
        Get(name) ?? throw new BuildException($"Missing required option --{name}.");

    /// <summary>失败时抛 <see cref="BuildException"/>,消息包含出错的选项。</summary>
    public static ParsedArgs Parse(CommandSpec spec, IReadOnlyList<string> args)
    {
        var result = new ParsedArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                throw new BuildException($"Unexpected argument '{arg}'. Options look like --name value.");
            }

            string name = arg[2..];
            string? inline = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                inline = name[(eq + 1)..];
                name = name[..eq];
            }

            if (Array.IndexOf(spec.Flags, name) >= 0)
            {
                if (inline is not null)
                {
                    throw new BuildException($"Option --{name} does not take a value.");
                }

                result._flags.Add(name);
                continue;
            }

            if (Array.IndexOf(spec.ValueOptions, name) < 0)
            {
                throw new BuildException($"Unknown option --{name} for '{spec.Name}'.");
            }

            if (inline is null)
            {
                if (i + 1 >= args.Count)
                {
                    throw new BuildException($"Option --{name} needs a value.");
                }

                inline = args[++i];
            }

            if (!result._values.TryGetValue(name, out var list))
            {
                result._values[name] = list = [];
            }

            list.Add(inline);
        }

        return result;
    }
}

/// <summary>各子命令共用的解析:RID、版本、产物目录。</summary>
internal static class CommonOptions
{
    public static Rid RidOrHost(ParsedArgs args, BuildContext ctx) =>
        args.Get("rid") is { } text ? Rid.Parse(text) : ctx.Host;

    public static string Version(ParsedArgs args, BuildContext ctx) =>
        args.Get("version") ?? ctx.Repo.ReadVersion();

    public static string Configuration(ParsedArgs args) => args.Get("configuration") ?? "Release";
}
