using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Git;

/// <summary>环境变量的合并(后者覆盖前者)。</summary>
internal static class GitEnvironment
{
    public static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string>? extra)
    {
        if (extra is null or { Count: 0 })
        {
            return baseline;
        }

        var merged = new Dictionary<string, string>(baseline, StringComparer.Ordinal);
        foreach (var (name, value) in extra)
        {
            merged[name] = value;
        }

        return merged;
    }
}

/// <summary>一个 Git 主机的 HTTPS 凭据(用户名 + 令牌)。<see cref="Host"/> 是 URL 里的 <c>host[:port]</c>(小写)。</summary>
public sealed record GitHttpCredential(string Host, string Username, string Token);

/// <summary>
/// 把一组 HTTPS 凭据变成 git 能用的环境变量(02 A6「Git 接力」):<c>http.https://&lt;host&gt;/.extraHeader = Authorization: Basic …</c>。
/// 按 URL 作用域:git 只会把这个头发给匹配的主机,所以多个主机的令牌互不泄漏;不在命令行、不在 <c>.git/config</c>、不在 URL 里。
/// </summary>
public static class GitCredentialEnvironment
{
    public static IReadOnlyDictionary<string, string> Build(IReadOnlyCollection<GitHttpCredential> credentials)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (credentials.Count == 0)
        {
            return env;
        }

        var n = 0;
        foreach (var credential in credentials)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Token}"));
            env[$"GIT_CONFIG_KEY_{n}"] = $"http.https://{credential.Host}/.extraHeader";
            env[$"GIT_CONFIG_VALUE_{n}"] = $"Authorization: Basic {basic}";
            n++;
        }

        env["GIT_CONFIG_COUNT"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return env;
    }
}

/// <summary>克隆请求。<see cref="Url"/> 必须先过 <see cref="GitCloner.TryParseUrl"/>。</summary>
public sealed record GitCloneRequest(Uri Url, string? Branch, IReadOnlyDictionary<string, string>? Environment, IReadOnlyCollection<string>? Secrets = null);

/// <summary>
/// 克隆一个远程仓库到执行环境里的目录(02 A6「工作区来源」)。<b>全部经 <see cref="IExecutionEnvironment"/></b>(AGENTS.md §5):git 以 argv 形式执行,不经 shell。
/// <list type="bullet">
/// <item>只接受 <c>https://</c> 地址、不带用户名 / 密码(凭据走 <see cref="GitCredentialEnvironment"/>),<c>GIT_ALLOW_PROTOCOL=https</c> 同时限制重定向和子模块的传输。</item>
/// <item>URL 放在 <c>--</c> 之后;分支名用 <see cref="GitService.SafeRef"/> 挡掉前导 <c>-</c>(flag 注入)。</item>
/// <item>目标目录必须不存在或为空;失败时删掉这次克隆创建的目录,不留半成品。</item>
/// <item>stderr 里出现的令牌一律抹掉(<see cref="GitCloneRequest.Secrets"/>)再放进异常。</item>
/// </list>
/// </summary>
public static class GitCloner
{
    public const string InvalidUrl = "projects.clone.url";
    public const string TargetNotEmpty = "projects.clone.targetNotEmpty";
    public const string TargetInvalid = "projects.clone.target";

    private static readonly IReadOnlyDictionary<string, string> NoPrompt = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"] = "",
        ["SSH_ASKPASS"] = "",
        ["GIT_ALLOW_PROTOCOL"] = "https",
    };

    /// <summary>解析克隆地址:<c>https://host[:port]/path</c>,不带用户信息 / 控制字符 / 片段,≤ 2048 字符。</summary>
    public static bool TryParseUrl(string? text, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 || text.Any(char.IsControl) || text.StartsWith('-'))
        {
            return false;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        url = uri;
        return true;
    }

    /// <summary>凭据表的键:小写的 <c>host[:port]</c>(默认端口省略),和 <see cref="GitHttpCredential.Host"/> 同一种形式。</summary>
    public static string HostKey(Uri url) => url.IsDefaultPort ? url.Host.ToLowerInvariant() : $"{url.Host.ToLowerInvariant()}:{url.Port}";

    public static async Task CloneAsync(IExecutionEnvironment environment, EnvPath target, GitCloneRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!target.IsAbsolute)
        {
            throw new GitException(GitErrorKind.InvalidRequest, TargetInvalid);
        }

        var branch = request.Branch is { Length: > 0 } b ? GitService.SafeRef(b) : null;
        var existed = false;
        try
        {
            var info = await environment.Fs.StatAsync(target, ct).ConfigureAwait(false);
            existed = true;
            if (!info.IsDirectory)
            {
                throw new GitException(GitErrorKind.InvalidRequest, TargetInvalid);
            }

            if ((await environment.Fs.ListAsync(target, ct).ConfigureAwait(false)).Count > 0)
            {
                throw new GitException(GitErrorKind.InvalidRequest, TargetNotEmpty);
            }
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.NotFound)
        {
            // 不存在:正好
        }

        var parent = target.Parent;
        await environment.Fs.MkdirAsync(parent, recursive: true, ct).ConfigureAwait(false);

        string[] args = branch is null
            ? ["clone", "--quiet", "--", request.Url.AbsoluteUri, target.Value]
            : ["clone", "--quiet", "--branch", branch, "--", request.Url.AbsoluteUri, target.Value];
        var spec = new CommandSpec { Program = "git", Args = args, WorkingDirectory = parent, Environment = GitEnvironment.Merge(NoPrompt, request.Environment) };
        ProcessResult result;
        try
        {
            result = await environment.RunAsync(spec, new RunOptions { Timeout = timeout, OutputPolicy = OutputPolicies.Head(64 * 1024) }, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.ProcessStartFailed)
        {
            throw new GitException(GitErrorKind.NotAvailable, GitKeys.Unavailable(GitUnavailableReason.GitMissing));
        }

        if (result.Outcome == ProcessOutcome.Cancelled)
        {
            await CleanUpAsync(environment, target, existed).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        if (result.Outcome == ProcessOutcome.TimedOut)
        {
            await CleanUpAsync(environment, target, existed).ConfigureAwait(false);
            throw new GitException(GitErrorKind.Unavailable, GitKeys.Timeout, "git clone timed out");
        }

        if (!result.Succeeded)
        {
            await CleanUpAsync(environment, target, existed).ConfigureAwait(false);
            var stderr = Redact(result.Stderr.ToText().Trim(), request.Secrets);
            throw new GitException(GitErrorKind.Failed, GitKeys.Failed, stderr.Length == 0 ? $"git exited with {result.ExitCode}" : stderr);
        }
    }

    private static async Task CleanUpAsync(IExecutionEnvironment environment, EnvPath target, bool existed)
    {
        try
        {
            if (existed)
            {
                // 目录本来就在(空的):清空里面的东西,留下目录
                foreach (var entry in await environment.Fs.ListAsync(target).ConfigureAwait(false))
                {
                    if (entry.Info.Kind == FsEntryKind.Directory)
                    {
                        await environment.Fs.DeleteDirectoryAsync(target.Join(entry.Name), recursive: true).ConfigureAwait(false);
                    }
                    else
                    {
                        await environment.Fs.DeleteFileAsync(target.Join(entry.Name)).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await environment.Fs.DeleteDirectoryAsync(target, recursive: true).ConfigureAwait(false);
            }
        }
        catch (EnvironmentException)
        {
            // 清理尽力而为:原始的克隆失败才是要告诉用户的
        }
    }

    private static string Redact(string text, IReadOnlyCollection<string>? secrets)
    {
        if (secrets is null)
        {
            return text;
        }

        foreach (var secret in secrets.Where(static s => s.Length >= 4))
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text;
    }
}
