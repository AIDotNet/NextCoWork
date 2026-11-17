using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Ncw.Agent.Environments;
using static Ncw.Agent.Terminal.WindowsNative;

namespace Ncw.Agent.Terminal;

/// <summary>
/// Windows 本机 PTY(ConPTY,<c>CreatePseudoConsole</c>,Windows 10 1809+)。
/// <list type="bullet">
/// <item>两条匿名管道接伪控制台的输入输出;子进程经 <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> 挂到它上面。</item>
/// <item><b>进程树</b>:子进程创建时就经 <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> 放进一个作业对象(<c>KILL_ON_JOB_CLOSE</c>),
/// 终止 = <c>TerminateJobObject</c>,整棵树一次杀掉,宿主崩溃时也不会留下孤儿。</item>
/// <item><b>退出</b>:shell 退出后伪控制台不会自己关管道,必须 <c>ClosePseudoConsole</c> 才会得到输出的 EOF;关闭放在另一个线程,
/// 读线程继续排空,避免旧版 Windows 上「关闭时输出管道没人读」的死锁。</item>
/// </list>
/// <b>未在 Windows 真机上验证</b>:写法照 EchoCon 示例,只做过编译检查(docs/design/06 §9)。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConPtyTerminal : PtyTerminal
{
    private readonly nint _console;
    private readonly nint _job;
    private readonly nint _process;
    private readonly nint _thread;
    private readonly FileStream _input;
    private readonly FileStream _outputStream;
    private readonly Thread _reader;
    private readonly Thread _waiter;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _consoleClosed;

    private ConPtyTerminal(nint console, nint job, nint process, nint thread, FileStream input, FileStream output)
    {
        _console = console;
        _job = job;
        _process = process;
        _thread = thread;
        _input = input;
        _outputStream = output;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ncw-conpty-read" };
        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "ncw-conpty-wait" };
    }

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public static unsafe ConPtyTerminal Start(PtyLaunch launch)
    {
        if (!IsSupported)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "ConPTY requires Windows 10 1809 or later");
        }

        nint inputRead = 0, inputWrite = 0, outputRead = 0, outputWrite = 0, console = 0, job = 0, attributeList = 0;
        var attributeListInitialized = false;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, 0, 0) || !CreatePipe(out outputRead, out outputWrite, 0, 0))
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"CreatePipe failed ({Marshal.GetLastPInvokeError()})");
            }

            var hr = CreatePseudoConsole(new Coord { X = (short)launch.Columns, Y = (short)launch.Rows }, inputRead, outputWrite, 0, out console);
            if (hr != 0)
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"CreatePseudoConsole failed (HRESULT 0x{hr:X8})");
            }

            // 伪控制台持有自己的副本:这两端我们不再需要
            CloseHandle(inputRead);
            inputRead = 0;
            CloseHandle(outputWrite);
            outputWrite = 0;

            job = CreateJobObjectW(0, null);
            if (job == 0)
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"CreateJobObject failed ({Marshal.GetLastPInvokeError()})");
            }

            var limits = default(JobObjectExtendedLimitInformation);
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, &limits, (uint)sizeof(JobObjectExtendedLimitInformation)))
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"SetInformationJobObject failed ({Marshal.GetLastPInvokeError()})");
            }

            nuint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size); // 第一次调用只取所需大小,按文档会「失败」
            attributeList = (nint)NativeMemory.AllocZeroed(size);
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref size))
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"InitializeProcThreadAttributeList failed ({Marshal.GetLastPInvokeError()})");
            }

            attributeListInitialized = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributePseudoConsole, (void*)console, (nuint)sizeof(nint), 0, 0))
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"UpdateProcThreadAttribute(PSEUDOCONSOLE) failed ({Marshal.GetLastPInvokeError()})");
            }

            var jobHandle = job;
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeJobList, &jobHandle, (nuint)sizeof(nint), 0, 0))
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"UpdateProcThreadAttribute(JOB_LIST) failed ({Marshal.GetLastPInvokeError()})");
            }

            var startup = default(StartupInfoExW);
            startup.StartupInfo.Cb = (uint)sizeof(StartupInfoExW);
            // ★ 必须设 STARTF_USESTDHANDLES(三个句柄留空):本进程的标准句柄被重定向时——ncw-agent 就是被桌面端用管道拉起的(stdin 生命线、stdout 的 ready 行),
            // 测试宿主也是——不设它,子进程会沿用父进程那套重定向的句柄而不是伪终端,终端里一个字都看不到(node-pty 的 conpty.cc 同样处理)。
            // 首次在 GitHub 的 Windows runner 上跑时,所有 ConPTY 用例都卡在「等不到第一行输出」。
            startup.StartupInfo.Flags = StartfUseStdHandles;
            startup.AttributeList = attributeList;

            var commandLine = (WindowsCommandLine.Build(launch.Program, launch.Args) + "\0").ToCharArray();
            var environment = WindowsCommandLine.EnvironmentBlock(launch.Environment);
            ProcessInformation info;
            bool created;
            fixed (char* commandLinePtr = commandLine)
            fixed (char* environmentPtr = environment)
            {
                created = CreateProcessW(null, commandLinePtr, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environmentPtr, launch.WorkingDirectory, &startup, out info);
            }

            if (!created)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new EnvironmentException(error is 2 or 3 ? EnvironmentErrorCode.NotFound : EnvironmentErrorCode.ProcessStartFailed, $"{launch.Program}: CreateProcess failed ({error})");
            }

            var input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 1, isAsync: false);
            inputWrite = 0;
            var output = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 1, isAsync: false);
            outputRead = 0;
            var terminal = new ConPtyTerminal(console, job, info.Process, info.Thread, input, output);
            console = 0;
            job = 0;
            terminal._reader.Start();
            terminal._waiter.Start();
            return terminal;
        }
        finally
        {
            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != 0)
            {
                NativeMemory.Free((void*)attributeList);
            }

            // 只有启动失败才会走到下面这些:成功路径上它们都已经移交并清零
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite })
            {
                if (handle != 0)
                {
                    CloseHandle(handle);
                }
            }

            if (console != 0)
            {
                ClosePseudoConsole(console);
            }

            if (job != 0)
            {
                CloseHandle(job);
            }
        }
    }

    internal override async ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.Length == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                _input.Write(data.Span);
                _input.Flush();
            }, CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        ValidateSize(columns, rows);
        if (Volatile.Read(ref _consoleClosed) == 1)
        {
            throw new IOException("terminal closed");
        }

        var hr = ResizePseudoConsole(_console, new Coord { X = (short)columns, Y = (short)rows });
        if (hr != 0)
        {
            throw new IOException($"ResizePseudoConsole failed (HRESULT 0x{hr:X8})");
        }

        return ValueTask.CompletedTask;
    }

    public override async ValueTask KillAsync(CancellationToken ct = default)
    {
        if (ProcessExited.IsCompleted)
        {
            return;
        }

        MarkKillRequested();
        TerminateJobObject(_job, 1);
        await ProcessExited.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, Infinite);
        _ = GetExitCodeProcess(_process, out var code);
        SetProcessExit(new ProcessExit((int)code, null, KillRequested ? ProcessExitReason.Killed : ProcessExitReason.Exited));

        // 给伪控制台一点时间把最后的输出写进管道,再关它;关闭在单独的线程里,读线程继续排空
        Thread.Sleep(100);
        CloseConsole();
    }

    private void CloseConsole()
    {
        if (Interlocked.Exchange(ref _consoleClosed, 1) == 1)
        {
            return;
        }

        ClosePseudoConsole(_console);
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            for (;;)
            {
                var count = _outputStream.Read(buffer, 0, buffer.Length);
                if (count <= 0)
                {
                    break;
                }

                Publish(buffer.AsSpan(0, count));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // 管道断开:输出结束
        }
        finally
        {
            CompleteOutput();
        }
    }

    protected override async ValueTask ReleaseAsync()
    {
        await Task.Run(() =>
        {
            CloseConsole();
            _waiter.Join(TimeSpan.FromSeconds(2));
            _reader.Join(TimeSpan.FromSeconds(2));
        }).ConfigureAwait(false);
        _input.Dispose();
        _outputStream.Dispose();
        CloseHandle(_thread);
        CloseHandle(_process);
        CloseHandle(_job); // KILL_ON_JOB_CLOSE:还活着的后代在这里被带走
        _writeLock.Dispose();
    }
}
