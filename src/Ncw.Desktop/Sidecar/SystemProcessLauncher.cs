using System.Diagnostics;

namespace Ncw.Desktop.Sidecar;

/// <summary>真实的进程启动。</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public IAgentProcess Start(AgentLaunchSpec spec, Action<string> onOutput)
    {
        var psi = new ProcessStartInfo(spec.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.Lifeline,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in spec.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in spec.Environment)
        {
            psi.Environment[key] = value;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput("[stderr] " + e.Data);
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new SystemAgentProcess(process, ownsStdin: spec.Lifeline);
    }

    public IAgentProcess? Attach(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            // pid 会被复用:只接上确实叫 ncw-agent 的进程,绝不去终止别的程序。
            if (process.HasExited || !process.ProcessName.StartsWith("ncw-agent", StringComparison.Ordinal))
            {
                process.Dispose();
                return null;
            }

            return new SystemAgentProcess(process, ownsStdin: false);
        }
        catch (ArgumentException)
        {
            return null; // 没有这个进程
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class SystemAgentProcess(Process process, bool ownsStdin) : IAgentProcess
    {
        public int Id { get; } = process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return process.HasExited ? process.ExitCode : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            try
            {
                return process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return -1; // 接上的、不是我们的子进程:拿不到退出码
            }
        }

        public void CloseStdin()
        {
            if (!ownsStdin)
            {
                return;
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        public bool RequestTerminate()
        {
            if (OperatingSystem.IsWindows())
            {
                return false;
            }

            // 不用 P/Invoke libc:Linux 上 "libc" 的解析在不同发行版上不一致(libc.so 可能是链接脚本)。/bin/kill 在 macOS 和 Linux 都有。
            try
            {
                using var kill = Process.Start(new ProcessStartInfo("/bin/kill", ["-TERM", Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                });
                kill?.WaitForExit(2000);
                return kill is { ExitCode: 0 };
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose() => process.Dispose();
    }
}
