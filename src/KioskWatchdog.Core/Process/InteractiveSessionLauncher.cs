using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Process;

/// <summary>
/// Starts a process in the active interactive user session (needed when the watchdog
/// runs as a Windows Service in Session 0 and must show a GUI / Electron app).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InteractiveSessionLauncher
{
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const int TokenDuplication = 2;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    public static bool IsRunningInSession0()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId)
               && sessionId == 0;
    }

    public static ProcessInfo Start(
        string executablePath,
        string arguments,
        string workingDirectory,
        ILogger? logger = null)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
            throw new InvalidOperationException("No active console session is available to launch the application.");

        if (!WTSQueryUserToken(sessionId, out var userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed. Is the kiosk user logged on?");

        var primaryToken = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var processInfo = new PROCESS_INFORMATION();

        try
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>()
            };

            if (!DuplicateTokenEx(
                    userToken,
                    0x10000000, // MAXIMUM_ALLOWED
                    ref sa,
                    SecurityImpersonation,
                    TokenPrimary,
                    out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");

            var commandLine = string.IsNullOrWhiteSpace(arguments)
                ? $"\"{executablePath}\""
                : $"\"{executablePath}\" {arguments}";

            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            var workDir = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;

            if (!CreateProcessAsUser(
                    primaryToken,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                    environment,
                    workDir,
                    ref startup,
                    out processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
            }

            logger?.LogInformation(
                "Started process in interactive session {Session} (PID {Pid}) from {Path}",
                sessionId,
                processInfo.dwProcessId,
                executablePath);

            DateTimeOffset? startTime = null;
            string processName = Path.GetFileNameWithoutExtension(executablePath);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processInfo.dwProcessId);
                processName = process.ProcessName;
                startTime = process.StartTime.ToUniversalTime();
            }
            catch
            {
                // Best-effort metadata only
            }

            return new ProcessInfo
            {
                Id = processInfo.dwProcessId,
                ProcessName = processName,
                StartTime = startTime,
                HasExited = false
            };
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero)
                CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero)
                CloseHandle(processInfo.hProcess);
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero)
                CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero)
                CloseHandle(userToken);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
