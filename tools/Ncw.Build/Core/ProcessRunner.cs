using System.Diagnostics;

namespace Ncw.Build.Core;

/// <param name="Redact">命令行里出现的这些字符串在日志里替换成 ***(密码、令牌)。</param>
internal sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Env = null,
    IReadOnlyList<string>? Redact = null)
{
    /// <summary>给日志看的命令行(已脱敏)。</summary>
    public string Display()
    {
        var parts = new[] { FileName }.Concat(Args).Select(Quote);
        var text = string.Join(' ', parts);
        foreach (var secret in Redact ?? [])
        {
            if (secret.Length > 0)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static string Quote(string s) => s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
}

/// <summary>退出码,加上 stdout 与 stderr 合并后的逐行输出。</summary>
internal sealed record ProcessResult(int ExitCode, IReadOnlyList<string> Lines);

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onLine = null, CancellationToken ct = default);
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
        };
        foreach (var arg in spec.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in spec.Env ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                psi.Environment.Remove(key);
            }
            else
            {
                psi.Environment[key] = value;
            }
        }

        var lines = new List<string>();
        void Receive(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (lines)
            {
                lines.Add(line);
            }

            onLine?.Invoke(line);
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Receive(e.Data);
        process.ErrorDataReceived += (_, e) => Receive(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BuildException($"Cannot start '{spec.FileName}': {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await using (ct.Register(() => TryKill(process)).ConfigureAwait(false))
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        // 带重定向的 WaitForExitAsync 返回后,异步读取可能还有尾巴:再等一次同步版本让缓冲排空。
        process.WaitForExit();
        lock (lines)
        {
            return new ProcessResult(process.ExitCode, lines.ToArray());
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
