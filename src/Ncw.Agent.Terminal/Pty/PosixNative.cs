using System.Runtime.InteropServices;

namespace Ncw.Agent.Terminal;

/// <summary>
/// POSIX 终端和进程相关的 libc 调用(LibraryImport 源生成)。
/// <para>
/// <b>为什么不用 forkpty</b>:fork 之后的子进程里只剩调用 fork 的那一个线程,而 .NET 运行时(GC、JIT、托管线程池)的状态是按多线程设计的,
/// 在托管代码里 fork 再 exec 会偶发死锁。所以这里只用 <c>posix_spawn</c>(由 libc 在子进程里完成 setsid / 打开从设备 / dup2 / chdir / 重置信号,
/// 托管代码一行都不会在子进程里跑),和 node-pty 那个独立的 <c>spawn-helper</c> 解决的是同一个问题,但不需要额外的原生可执行文件。
/// </para>
/// <para>
/// <b>ioctl 是变参函数</b>:Apple Silicon 的 ABI 把变参一律放在栈上,而 P/Invoke 按普通函数把第三个参数放进寄存器,所以 macOS arm64 上
/// 用「前面补 6 个占位参数、真正的参数落到栈上第一个槽」的声明(<see cref="IoctlStackArg"/>);其它平台按寄存器传参,两种 ABI 都不需要原生垫片。
/// </para>
/// </summary>
internal static unsafe partial class PosixNative
{
    private const string Libc = "libc";

    public const int ORdwr = 2;

    // 这些标志位在 Linux(x64 / arm64,glibc 与 musl)和 macOS 上取值不同
    public static int ONoCtty => OperatingSystem.IsMacOS() ? 0x20000 : 0x100;

    public static int OCloexec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    public static ulong TiocSwinsz => OperatingSystem.IsMacOS() ? 0x80087467UL : 0x5414UL;

    // posix_spawn 属性位
    public const short SpawnSetSigDef = 0x04;

    public const short SpawnSetSigMask = 0x08;

    /// <summary>macOS 另有 <c>POSIX_SPAWN_CLOEXEC_DEFAULT</c>:子进程只保留文件动作里明确给的 fd,其它全部关闭。</summary>
    public const int SpawnCloexecDefaultMac = 0x4000;

    public static int SpawnSetSid => OperatingSystem.IsMacOS() ? 0x400 : 0x80;

    public const short PollIn = 0x1;

    public const int Eintr = 4;

    public static int Eagain => OperatingSystem.IsMacOS() ? 35 : 11;

    public const int Echild = 10;

    public const int SigHup = 1;

    public const int SigKill = 9;

    public const int SigTerm = 15;

    public const int SigInt = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [LibraryImport(Libc, EntryPoint = "posix_openpt", SetLastError = true)]
    public static partial int posix_openpt(int flags);

    [LibraryImport(Libc, EntryPoint = "grantpt", SetLastError = true)]
    public static partial int grantpt(int fd);

    [LibraryImport(Libc, EntryPoint = "unlockpt", SetLastError = true)]
    public static partial int unlockpt(int fd);

    [LibraryImport(Libc, EntryPoint = "ptsname")]
    public static partial byte* ptsname(int fd);

    [LibraryImport(Libc, EntryPoint = "ptsname_r")]
    public static partial int ptsname_r(int fd, byte* buffer, nuint length);

    // open 是变参函数(第三个参数是创建文件的 mode),这里只用两个固定参数
    [LibraryImport(Libc, EntryPoint = "open", SetLastError = true)]
    public static partial int open(byte* path, int flags);

    [LibraryImport(Libc, EntryPoint = "close", SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport(Libc, EntryPoint = "read", SetLastError = true)]
    public static partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport(Libc, EntryPoint = "write", SetLastError = true)]
    public static partial nint write(int fd, byte* buffer, nuint count);

    [LibraryImport(Libc, EntryPoint = "poll", SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint count, int timeoutMs);

    [LibraryImport(Libc, EntryPoint = "kill", SetLastError = true)]
    public static partial int kill(int pid, int signal);

    [LibraryImport(Libc, EntryPoint = "waitpid", SetLastError = true)]
    public static partial int waitpid(int pid, int* status, int options);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, void* argument);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlStackArg(int fd, nuint request, nint p2, nint p3, nint p4, nint p5, nint p6, nint p7, void* argument);

    /// <summary>设置终端窗口大小(<c>TIOCSWINSZ</c>)。成功返回 0,失败返回 -1(errno 见 <see cref="Marshal.GetLastPInvokeError"/>)。</summary>
    public static int SetWindowSize(int fd, WinSize size) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? IoctlStackArg(fd, (nuint)TiocSwinsz, 0, 0, 0, 0, 0, 0, &size)
            : ioctl(fd, (nuint)TiocSwinsz, &size);

    // ───────── posix_spawn ─────────
    // posix_spawn_file_actions_t / posix_spawnattr_t 在 macOS 上是一个指针,在 glibc 上是几百字节的结构:一律给一块足够大的清零内存

    public const int SpawnStructBytes = 1024;

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_init")]
    public static partial int posix_spawn_file_actions_init(void* actions);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_destroy")]
    public static partial int posix_spawn_file_actions_destroy(void* actions);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_addopen")]
    public static partial int posix_spawn_file_actions_addopen(void* actions, int fd, byte* path, int flags, ushort mode);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_adddup2")]
    public static partial int posix_spawn_file_actions_adddup2(void* actions, int fd, int newFd);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    public static partial int posix_spawn_file_actions_addchdir_np(void* actions, byte* path);

    [LibraryImport(Libc, EntryPoint = "posix_spawnattr_init")]
    public static partial int posix_spawnattr_init(void* attributes);

    [LibraryImport(Libc, EntryPoint = "posix_spawnattr_destroy")]
    public static partial int posix_spawnattr_destroy(void* attributes);

    [LibraryImport(Libc, EntryPoint = "posix_spawnattr_setflags")]
    public static partial int posix_spawnattr_setflags(void* attributes, short flags);

    [LibraryImport(Libc, EntryPoint = "posix_spawnattr_setsigdefault")]
    public static partial int posix_spawnattr_setsigdefault(void* attributes, void* signals);

    [LibraryImport(Libc, EntryPoint = "posix_spawnattr_setsigmask")]
    public static partial int posix_spawnattr_setsigmask(void* attributes, void* signals);

    [LibraryImport(Libc, EntryPoint = "sigfillset")]
    public static partial int sigfillset(void* set);

    [LibraryImport(Libc, EntryPoint = "sigemptyset")]
    public static partial int sigemptyset(void* set);

    [LibraryImport(Libc, EntryPoint = "posix_spawn")]
    public static partial int posix_spawn(int* pid, byte* path, void* actions, void* attributes, byte** argv, byte** envp);

    [LibraryImport(Libc, EntryPoint = "posix_spawnp")]
    public static partial int posix_spawnp(int* pid, byte* path, void* actions, void* attributes, byte** argv, byte** envp);
}
