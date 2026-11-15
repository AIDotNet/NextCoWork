using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ncw.Agent.Terminal;

/// <summary>
/// ConPTY(Windows 10 1809+ 的伪控制台)与作业对象的 kernel32 调用。
/// <b>未在 Windows 真机上验证</b>(见 docs/design/06 §9):写法照 Microsoft 的 EchoCon 示例和 Windows Terminal 的做法,只做过编译检查。
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsNative
{
    private const string Kernel32 = "kernel32";

    public const uint ExtendedStartupInfoPresent = 0x00080000;

    public const uint CreateUnicodeEnvironment = 0x00000400;

    public const nuint ProcThreadAttributePseudoConsole = 0x00020016;

    public const nuint ProcThreadAttributeJobList = 0x0002000D;

    public const uint Infinite = 0xFFFFFFFF;

    public const int JobObjectExtendedLimitInformationClass = 9;

    public const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoW
    {
        public uint Cb;
        public char* Reserved;
        public char* Desktop;
        public char* Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public byte* Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoExW
    {
        public StartupInfoW StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport(Kernel32, EntryPoint = "CreatePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out nint readPipe, out nint writePipe, nint attributes, uint size);

    [LibraryImport(Kernel32, EntryPoint = "CreatePseudoConsole")]
    public static partial int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);

    [LibraryImport(Kernel32, EntryPoint = "ResizePseudoConsole")]
    public static partial int ResizePseudoConsole(nint console, Coord size);

    [LibraryImport(Kernel32, EntryPoint = "ClosePseudoConsole")]
    public static partial void ClosePseudoConsole(nint console);

    [LibraryImport(Kernel32, EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(nint attributeList, uint attributeCount, uint flags, ref nuint size);

    [LibraryImport(Kernel32, EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nuint attribute, void* value, nuint size, nint previousValue, nint returnSize);

    [LibraryImport(Kernel32, EntryPoint = "DeleteProcThreadAttributeList")]
    public static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(string? applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, char* environment, string? currentDirectory, StartupInfoExW* startupInfo, out ProcessInformation processInformation);

    [LibraryImport(Kernel32, EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport(Kernel32, EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint size);

    [LibraryImport(Kernel32, EntryPoint = "TerminateJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport(Kernel32, EntryPoint = "WaitForSingleObject", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport(Kernel32, EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport(Kernel32, EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}
