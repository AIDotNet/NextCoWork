using System.Collections;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal;

public sealed class LocalPtyOptions
{
    /// <summary>
    /// 额外注入的环境变量(例如跟随系统代理)。合并次序同 <see cref="LocalEnvironmentOptions.ChildEnvironment"/>:
    /// 继承 ← 注入(不覆盖用户自己已有的同名变量)← <c>TERM</c> ← 终端规格里显式给的。
    /// 一般和 <see cref="LocalEnvironmentOptions.ChildEnvironment"/> 传同一个委托。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>>? ChildEnvironment { get; init; }

    /// <summary>终止进程树时,从礼貌请求(SIGHUP / SIGTERM)到强杀(SIGKILL)之间的宽限期。Windows 用作业对象整树强杀,不用它。</summary>
    public TimeSpan KillGrace { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// 本机 PTY 的入口:<see cref="CreateFactory"/> 返回的委托就是 <see cref="LocalEnvironmentOptions.TerminalFactory"/> 的注入物。
/// <code>
/// new LocalEnvironment(deviceId, new LocalEnvironmentOptions { TerminalFactory = LocalTerminals.CreateFactory() });
/// </code>
/// </summary>
public static class LocalTerminals
{
    /// <summary>这台机器上能不能开本机 PTY(POSIX 恒为真;Windows 需要 10 1809+ 的 ConPTY)。</summary>
    public static bool IsSupported => !OperatingSystem.IsWindows() || ConPtySupported();

    public static Func<TerminalSpec, EnvironmentFacts, CancellationToken, Task<ITerminal>> CreateFactory(LocalPtyOptions? options = null)
    {
        options ??= new LocalPtyOptions();
        return (spec, facts, ct) => OpenAsync(spec, facts, options, ct);
    }

    public static Task<ITerminal> OpenAsync(TerminalSpec spec, EnvironmentFacts facts, LocalPtyOptions? options = null, CancellationToken ct = default)
    {
        options ??= new LocalPtyOptions();
        try
        {
            ct.ThrowIfCancellationRequested();
            var launch = PtyLaunch.Resolve(spec, facts, options.ChildEnvironment?.Invoke(), InheritedEnvironment());
            ITerminal terminal = OperatingSystem.IsWindows()
                ? StartWindows(launch)
                : PosixPtyTerminal.Start(launch, options.KillGrace);
            return Task.FromResult(terminal);
        }
        catch (Exception e)
        {
            return Task.FromException<ITerminal>(e);
        }
    }

    private static ITerminal StartWindows(PtyLaunch launch)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return ConPtyTerminal.Start(launch);
    }

    private static bool ConPtySupported() => OperatingSystem.IsWindows() && ConPtyTerminal.IsSupported;

    private static IEnumerable<KeyValuePair<string, string>> InheritedEnvironment()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                yield return new KeyValuePair<string, string>(name, value);
            }
        }
    }
}
