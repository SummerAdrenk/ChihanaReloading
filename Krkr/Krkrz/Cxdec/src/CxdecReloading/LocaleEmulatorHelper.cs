using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace CxdecReloading;

/// <summary>
/// Locale Emulator 注入启动器
/// 复刻 LEProc 的核心逻辑：挂起创建进程 → 共享内存写配置 → 注入 LoaderDll.dll → 恢复
/// </summary>
public static class LocaleEmulatorHelper
{
    #region LE 配置结构体

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day;
        public ushort Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string StandardName;
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DaylightName;
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LeStartupInfo
    {
        public uint Size;
        public uint RunAsAdmin;
        public uint DebugMode;
        public uint SuspendMode;
        public uint RedirectRegistry;
        public uint HookUILanguageAPI;
        public uint AnsiCodePage;
        public uint OemCodePage;
        public uint LocaleID;
        public uint DefaultCharset;
        public uint DefaultHKL;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DefaultFaceName;
        public TIME_ZONE_INFORMATION Timezone;
        public uint NumberOfRegistryRedirectionEntries;
    }

    #endregion

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize;
        public uint dwXCountChars, dwYCountChars;
        public uint dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
        uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress,
        uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
        uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter,
        uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern void TerminateProcess(IntPtr hProcess, uint uExitCode);

    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    #endregion

    /// <summary>
    /// 使用 Locale Emulator 以日语区域启动目标进程
    /// </summary>
    /// <returns>(进程ID, 错误信息)，成功时错误为 null</returns>
    public static (int processId, string? error) LaunchWithLE(
        string exePath, string workDir, string loaderDllPath,
        string? arguments = null)
    {
        var loaderDllFullPath = Path.GetFullPath(loaderDllPath);
        if (!File.Exists(loaderDllFullPath))
            return (-1, $"LoaderDll.dll 不存在: {loaderDllFullPath}");

        // 日语区域配置
        var info = new LeStartupInfo
        {
            RunAsAdmin = 0,
            DebugMode = 0,
            SuspendMode = 0,
            RedirectRegistry = 1,
            HookUILanguageAPI = 1,
            AnsiCodePage = 932,
            OemCodePage = 932,
            LocaleID = 0x0411,
            DefaultCharset = 128, // SHIFTJIS_CHARSET
            DefaultHKL = 0x04110411,
            DefaultFaceName = "MS UI Gothic",
            Timezone = new TIME_ZONE_INFORMATION
            {
                Bias = -540, // UTC+9
                StandardName = "Tokyo Standard Time",
                DaylightName = "Tokyo Daylight Time",
            },
            NumberOfRegistryRedirectionEntries = 0,
        };
        info.Size = (uint)Marshal.SizeOf<LeStartupInfo>();

        // 1. 挂起方式创建游戏进程
        var si = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
        var cmdLine = arguments != null ? $"\"{exePath}\" {arguments}" : null;
        if (!CreateProcessW(exePath, cmdLine, IntPtr.Zero, IntPtr.Zero,
                false, CREATE_SUSPENDED, IntPtr.Zero, workDir, ref si, out var pi))
        {
            return (-1, $"CreateProcess 失败 (Win32 Error: {Marshal.GetLastWin32Error()})");
        }

        try
        {
            // 2. 写入 LE 配置到具名共享内存（LoaderDll.dll 在 DllMain 中读取）
            var mmfName = $"LEShareMemory-{pi.dwProcessId}";
            var infoSize = Marshal.SizeOf<LeStartupInfo>();

            using var mmf = MemoryMappedFile.CreateNew(mmfName, infoSize);
            using var accessor = mmf.CreateViewAccessor(0, infoSize);

            var buffer = new byte[infoSize];
            var ptr = Marshal.AllocHGlobal(infoSize);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                Marshal.Copy(ptr, buffer, 0, infoSize);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            accessor.WriteArray(0, buffer, 0, buffer.Length);

            // 3. 注入 LoaderDll.dll（CreateRemoteThread + LoadLibraryW）
            var injectError = InjectDll(pi.hProcess, loaderDllFullPath);
            if (injectError != null)
            {
                TerminateProcess(pi.hProcess, 1);
                return (-1, injectError);
            }

            // 4. 恢复主线程，游戏开始运行（此时 LE 已生效）
            ResumeThread(pi.hThread);

            return ((int)pi.dwProcessId, null);
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    private static string? InjectDll(IntPtr hProcess, string dllPath)
    {
        var dllPathBytes = Encoding.Unicode.GetBytes(dllPath + '\0');

        var remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero,
            (uint)dllPathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

        if (remoteMem == IntPtr.Zero)
            return $"VirtualAllocEx 失败 (Win32 Error: {Marshal.GetLastWin32Error()})";

        try
        {
            if (!WriteProcessMemory(hProcess, remoteMem, dllPathBytes,
                    (uint)dllPathBytes.Length, out _))
                return $"WriteProcessMemory 失败 (Win32 Error: {Marshal.GetLastWin32Error()})";

            var kernel32 = GetModuleHandleW("kernel32.dll");
            var loadLibraryW = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibraryW == IntPtr.Zero)
                return "无法获取 LoadLibraryW 地址";

            var thread = CreateRemoteThread(hProcess, IntPtr.Zero, 0,
                loadLibraryW, remoteMem, 0, out _);
            if (thread == IntPtr.Zero)
                return $"CreateRemoteThread 失败 (Win32 Error: {Marshal.GetLastWin32Error()})";

            // 等待 LoadLibraryW 完成（DllMain 中会读取共享内存并初始化 LE）
            WaitForSingleObject(thread, 10000);
            CloseHandle(thread);
        }
        finally
        {
            VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
        }

        return null;
    }
}