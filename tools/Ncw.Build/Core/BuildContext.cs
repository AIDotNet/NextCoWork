namespace Ncw.Build.Core;

/// <summary>子命令的运行环境:全部可替换,单测里换成假的。</summary>
internal sealed record BuildContext(
    RepoLayout Repo,
    IProcessRunner Runner,
    IBuildLog Log,
    Func<string, string?> Env,
    TimeProvider Clock,
    Rid Host)
{
    public static BuildContext Create(RepoLayout repo) =>
        new(repo, new SystemProcessRunner(), new ConsoleBuildLog(), Environment.GetEnvironmentVariable, TimeProvider.System, Rid.Host());

    /// <summary>环境变量非空才算设置了(CI 里空的 secret 会变成空字符串)。</summary>
    public string? EnvNonEmpty(string name) => Env(name) is { Length: > 0 } value ? value : null;
}
