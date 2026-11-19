using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Ncw.Agent.Environments;
using static Ncw.Agent.Terminal.PosixNative;

namespace Ncw.Agent.Terminal;

/// <summary>
/// POSIX(Linux / macOS)本机 PTY。
/// <list type="bullet">
/// <item>创建:<c>posix_openpt</c> / <c>grantpt</c> / <c>unlockpt</c> 拿到主设备,<c>posix_spawn</c> 带 <c>SETSID</c> 属性启动 shell,
/// 文件动作在子进程里(setsid 之后)打开从设备并 dup 到 0/1/2 ——「会话首进程打开一个还没有控制终端的 tty」就是它的控制终端,
/// 所以 Ctrl-C、作业控制、<c>SIGWINCH</c> 都按真终端工作。</item>
/// <item>读:一个专用线程 <c>poll</c> + <c>read</c>(50ms 超时只用来检查停止标志),不占线程池。</item>
/// <item>退出:另一个专用线程 <c>waitpid</c>;退出码、信号都来自内核。</item>
/// <item>终止:按「后代快照 + 进程组」发 SIGHUP / SIGTERM,宽限期后 SIGKILL。shell 自己是进程组组长,所以不需要快照也能杀掉前台任务;
/// 作业控制开出来的其它进程组靠快照。</item>
/// </list>
/// </summary>
internal sealed class PosixPtyTerminal : PtyTerminal
{
    private readonly MasterHandle _master;
    private readonly int _pid;
    private readonly TimeSpan _grace;
    private readonly Thread _reader;
    private readonly Thread _waiter;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _childExitedTick;

    private PosixPtyTerminal(MasterHandle master, int pid, TimeSpan grace)
    {
        _master = master;
        _pid = pid;
        _grace = grace;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ncw-pty-read" };
        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "ncw-pty-wait" };
    }

    public int Pid => _pid;

    public static PosixPtyTerminal Start(PtyLaunch launch, TimeSpan grace)
    {
        var fd = posix_openpt(ORdwr | ONoCtty | OCloexec);
        if (fd < 0 && Marshal.GetLastPInvokeError() == 22)
        {
            // EINVAL:这个系统的 posix_openpt 不认 O_CLOEXEC(macOS 靠 SpawnCloexecDefault 兜底)
            fd = posix_openpt(ORdwr | ONoCtty);
        }

        if (fd < 0)
        {
            throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"posix_openpt failed (errno {Marshal.GetLastPInvokeError()})");
        }

        var master = new MasterHandle(fd);
        try
        {
            if (grantpt(fd) != 0 || unlockpt(fd) != 0)
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"grantpt/unlockpt failed (errno {Marshal.GetLastPInvokeError()})");
            }

            var slave = SlaveName(fd);
            // 窗口大小要在 shell 起来之前就设好,它启动时才读到正确的行列数。macOS 上对「还没有人打开从设备」的主设备做 TIOCSWINSZ 会 ENOTTY,
            // 所以父进程先自己打开从设备(带 O_NOCTTY:父进程不会因此取得控制终端)设好大小;子进程启动完成(posix_spawn 返回时
            // 它的文件动作已经执行完)之后立刻关掉,否则子进程退出时读端收不到 EOF
            var slaveFd = OpenSlave(slave);
            int pid;
            try
            {
                SetSize(slaveFd, launch.Columns, launch.Rows);
                pid = Spawn(launch, slave);
            }
            finally
            {
                close(slaveFd);
            }

            var terminal = new PosixPtyTerminal(master, pid, grace);
            terminal._reader.Start();
            terminal._waiter.Start();
            return terminal;
        }
        catch
        {
            master.Dispose();
            throw;
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
            // 写到子进程不读的终端时 write 会阻塞(内核里的行缓冲满了),所以放到线程池里;子进程被杀后会以 EIO 返回
            await Task.Run(() => WriteAll(data.Span), CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private unsafe void WriteAll(ReadOnlySpan<byte> data)
    {
        var added = false;
        try
        {
            _master.DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException)
        {
            throw new IOException("terminal closed");
        }

        try
        {
            var fd = (int)_master.DangerousGetHandle();
            fixed (byte* start = data)
            {
                var offset = 0;
                while (offset < data.Length)
                {
                    var written = write(fd, start + offset, (nuint)(data.Length - offset));
                    if (written < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno == Eintr || errno == Eagain)
                        {
                            continue;
                        }

                        throw new IOException($"terminal write failed (errno {errno})");
                    }

                    offset += (int)written;
                }
            }
        }
        finally
        {
            if (added)
            {
                _master.DangerousRelease();
            }
        }
    }

    public override ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        ValidateSize(columns, rows);
        var added = false;
        try
        {
            _master.DangerousAddRef(ref added);
            SetSize((int)_master.DangerousGetHandle(), columns, rows);
        }
        catch (ObjectDisposedException)
        {
            throw new IOException("terminal closed");
        }
        finally
        {
            if (added)
            {
                _master.DangerousRelease();
            }
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
        await Task.Run(() => TerminateTree(ct), CancellationToken.None).ConfigureAwait(false);
        await ProcessExited.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    private void TerminateTree(CancellationToken ct)
    {
        // 先拍快照再发信号:shell 先死了,它的子孙也还在快照里
        var descendants = ProcessTable.Descendants(_pid);
        Signal(descendants, SigHup);
        Signal(descendants, SigTerm);
        if (!ChildExited)
        {
            kill(_pid, SigHup);
            kill(-_pid, SigHup);
            kill(_pid, SigTerm);
            kill(-_pid, SigTerm);
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(_grace.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline && !ct.IsCancellationRequested && (!ChildExited || descendants.Any(Alive)))
        {
            Thread.Sleep(20);
        }

        // 宽限期里新派生的后代也要带走
        var all = descendants.Union(ProcessTable.Descendants(_pid)).ToList();
        if (!ChildExited)
        {
            kill(-_pid, SigKill);
            kill(_pid, SigKill);
        }

        foreach (var pid in all)
        {
            if (Alive(pid))
            {
                kill(pid, SigKill);
            }
        }
    }

    private bool ChildExited => Interlocked.Read(ref _childExitedTick) != 0;

    private static bool Alive(int pid) => kill(pid, 0) == 0;

    private static void Signal(IReadOnlyList<int> pids, int signal)
    {
        foreach (var pid in pids)
        {
            kill(pid, signal);
        }
    }

    protected override async ValueTask ReleaseAsync()
    {
        await Task.Run(() =>
        {
            _reader.Join(TimeSpan.FromSeconds(2));
            _waiter.Join(TimeSpan.FromSeconds(2));
        }).ConfigureAwait(false);
        _master.Dispose();
        // _writeLock 故意不 Dispose:shell 因为刚写进去的输入而退出(`exit`)时,终端在写入收尾(Release)之前就被释放,
        // 对已释放的 SemaphoreSlim 操作会抛 ObjectDisposedException(CI 的 macOS 上撞到过)。没取过 AvailableWaitHandle 的 SemaphoreSlim 不持有系统资源;
        // 关闭之后再来的写入照常拿到锁,在底层句柄上得到「terminal closed」。
    }

    private unsafe void WaitLoop()
    {
        int status;
        ProcessExit exit;
        for (;;)
        {
            var result = waitpid(_pid, &status, 0);
            if (result == _pid)
            {
                var signal = status & 0x7f;
                exit = signal == 0
                    ? new ProcessExit((status >> 8) & 0xff, null, KillRequested ? ProcessExitReason.Killed : ProcessExitReason.Exited)
                    : new ProcessExit(128 + signal, signal, KillRequested ? ProcessExitReason.Killed : ProcessExitReason.Exited);
                break;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Eintr)
            {
                continue;
            }

            // ECHILD(Echild):子进程已被别人回收(宿主把 SIGCHLD 设成了忽略)——退出码未知
            exit = new ProcessExit(null, null, KillRequested ? ProcessExitReason.Killed : ProcessExitReason.Exited);
            break;
        }

        Interlocked.Exchange(ref _childExitedTick, Stopwatch.GetTimestamp());
        SetProcessExit(exit);
    }

    private unsafe void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        var idleAfterExit = 0;
        try
        {
            for (;;)
            {
                if (DisposedToken.IsCancellationRequested)
                {
                    break;
                }

                var added = false;
                try
                {
                    _master.DangerousAddRef(ref added);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                try
                {
                    var fd = (int)_master.DangerousGetHandle();
                    var poller = new PollFd { Fd = fd, Events = PollIn };
                    var ready = poll(&poller, 1, 50);
                    if (ready < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == Eintr)
                        {
                            continue;
                        }

                        break;
                    }

                    if (ready == 0)
                    {
                        // 子进程已退出后:一直没有新输出就认为读完了(后台进程攥着从设备时不会有 EOF)
                        if (ChildExited && ++idleAfterExit >= 2)
                        {
                            break;
                        }

                        continue;
                    }

                    nint count;
                    fixed (byte* start = buffer)
                    {
                        count = read(fd, start, (nuint)buffer.Length);
                    }

                    if (count > 0)
                    {
                        idleAfterExit = 0;
                        Publish(buffer.AsSpan(0, (int)count));
                        if (ChildExited && Stopwatch.GetElapsedTime(Interlocked.Read(ref _childExitedTick)) > TimeSpan.FromSeconds(2))
                        {
                            break; // 后台进程还在源源不断地写:不等它
                        }

                        continue;
                    }

                    if (count < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno == Eintr || errno == Eagain)
                        {
                            continue;
                        }
                    }

                    break; // EOF(0)或 EIO(从设备的最后一个持有者退出了)
                }
                finally
                {
                    if (added)
                    {
                        _master.DangerousRelease();
                    }
                }
            }
        }
        finally
        {
            CompleteOutput();
        }
    }

    private static void SetSize(int fd, int columns, int rows)
    {
        var size = new WinSize { Rows = (ushort)rows, Cols = (ushort)columns };
        if (SetWindowSize(fd, size) != 0)
        {
            throw new IOException($"TIOCSWINSZ failed (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    private unsafe static string SlaveName(int fd)
    {
        if (OperatingSystem.IsLinux())
        {
            byte* name = stackalloc byte[256];
            if (ptsname_r(fd, name, 256) != 0)
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, "ptsname_r failed");
            }

            return Marshal.PtrToStringUTF8((nint)name)!;
        }

        // macOS 没有 ptsname_r;ptsname 用一块静态缓冲,同进程里的并发创建串行化
        lock (SlaveNameLock)
        {
            var name = ptsname(fd);
            if (name is null)
            {
                throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, "ptsname failed");
            }

            return Marshal.PtrToStringUTF8((nint)name)!;
        }
    }

    private static readonly Lock SlaveNameLock = new();

    private static unsafe int OpenSlave(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        int fd;
        fixed (byte* name = bytes)
        {
            fd = open(name, ORdwr | ONoCtty | OCloexec);
            if (fd < 0)
            {
                fd = open(name, ORdwr | ONoCtty);
            }
        }

        if (fd < 0)
        {
            throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"open({path}) failed (errno {Marshal.GetLastPInvokeError()})");
        }

        return fd;
    }

    private unsafe static int Spawn(PtyLaunch launch, string slavePath)
    {
        using var strings = new NativeStrings();
        var program = launch.Program;
        var args = new List<string> { program };
        args.AddRange(launch.Args);
        var useWrapper = false;

        var actions = NativeMemory.AllocZeroed(SpawnStructBytes);
        var attributes = NativeMemory.AllocZeroed(SpawnStructBytes);
        var signals = NativeMemory.AllocZeroed(SpawnStructBytes);
        var actionsInitialized = false;
        var attributesInitialized = false;
        try
        {
            Check(posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            actionsInitialized = true;
            Check(posix_spawnattr_init(attributes), "posix_spawnattr_init");
            attributesInitialized = true;

            // 从设备在子进程里、setsid 之后才打开:它成为新会话的控制终端
            Check(posix_spawn_file_actions_addopen(actions, 0, strings.Add(slavePath), ORdwr, 0), "addopen");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1), "adddup2");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2), "adddup2");

            if (launch.WorkingDirectory is { } cwd)
            {
                try
                {
                    Check(posix_spawn_file_actions_addchdir_np(actions, strings.Add(cwd)), "addchdir_np");
                }
                catch (EntryPointNotFoundException)
                {
                    // 老 glibc(< 2.29)没有它:经 sh 先 cd 再 exec
                    useWrapper = true;
                }
            }

            // 运行时把 SIGPIPE 等设成了忽略,被忽略的信号会跨 exec 保留:重置成默认,并清空信号屏蔽
            sigfillset(signals);
            Check(posix_spawnattr_setsigdefault(attributes, signals), "setsigdefault");
            sigemptyset(signals);
            Check(posix_spawnattr_setsigmask(attributes, signals), "setsigmask");
            var flags = SpawnSetSid | SpawnSetSigDef | SpawnSetSigMask;
            if (OperatingSystem.IsMacOS())
            {
                flags |= SpawnCloexecDefaultMac;
            }

            Check(posix_spawnattr_setflags(attributes, (short)flags), "setflags");

            if (useWrapper)
            {
                // sh -c 'cd -- "$0" && exec "$@"' <dir> <program> <args...>
                args.Insert(0, launch.WorkingDirectory!);
                args.Insert(0, "cd -- \"$0\" && exec \"$@\"");
                args.Insert(0, "-c");
                args.Insert(0, "sh");
                program = "/bin/sh";
            }

            var argv = (byte**)NativeMemory.AllocZeroed((nuint)((args.Count + 1) * sizeof(byte*)));
            var envp = (byte**)NativeMemory.AllocZeroed((nuint)((launch.Environment.Count + 1) * sizeof(byte*)));
            try
            {
                for (var i = 0; i < args.Count; i++)
                {
                    argv[i] = strings.Add(args[i]);
                }

                for (var i = 0; i < launch.Environment.Count; i++)
                {
                    envp[i] = strings.Add(launch.Environment[i].Key + "=" + launch.Environment[i].Value);
                }

                int pid;
                var path = strings.Add(program);
                var result = program.Contains('/')
                    ? posix_spawn(&pid, path, actions, attributes, argv, envp)
                    : posix_spawnp(&pid, path, actions, attributes, argv, envp);
                if (result != 0)
                {
                    // 返回值就是 errno:2 = ENOENT,13 = EACCES
                    throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"{launch.Program}: posix_spawn failed (errno {result})");
                }

                return pid;
            }
            finally
            {
                NativeMemory.Free(argv);
                NativeMemory.Free(envp);
            }
        }
        finally
        {
            if (actionsInitialized)
            {
                posix_spawn_file_actions_destroy(actions);
            }

            if (attributesInitialized)
            {
                posix_spawnattr_destroy(attributes);
            }

            NativeMemory.Free(actions);
            NativeMemory.Free(attributes);
            NativeMemory.Free(signals);
        }
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"{what} failed (errno {result})");
        }
    }

    /// <summary>一批以 NUL 结尾的 UTF-8 原生字符串,释放时一起回收。</summary>
    private sealed unsafe class NativeStrings : IDisposable
    {
        private readonly List<nint> _blocks = [];

        public byte* Add(string value)
        {
            var count = Encoding.UTF8.GetByteCount(value);
            var block = (byte*)NativeMemory.Alloc((nuint)count + 1);
            Encoding.UTF8.GetBytes(value, new Span<byte>(block, count));
            block[count] = 0;
            _blocks.Add((nint)block);
            return block;
        }

        public void Dispose()
        {
            foreach (var block in _blocks)
            {
                NativeMemory.Free((void*)block);
            }
        }
    }

    private sealed class MasterHandle : SafeHandleMinusOneIsInvalid
    {
        public MasterHandle(int fd)
            : base(ownsHandle: true)
        {
            SetHandle(fd);
        }

        protected override bool ReleaseHandle() => close((int)handle) == 0;
    }
}
