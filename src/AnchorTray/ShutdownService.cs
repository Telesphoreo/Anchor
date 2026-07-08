using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Anchor;

/// <summary>
/// Initiates a graceful Windows shutdown: enables SeShutdownPrivilege on the
/// process token, then calls InitiateSystemShutdownExW. Retries once after
/// 10 seconds on failure.
/// </summary>
internal static class ShutdownService
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    // SHTDN_REASON_FLAG_PLANNED | SHTDN_REASON_MAJOR_POWER.
    private const uint ShutdownReason = 0x80060000;

    private const string ShutdownMessage = "Anchor: shutting down — power station battery at floor.";

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InitiateSystemShutdownExW(
        string? machineName,
        string? message,
        uint timeout,
        bool forceAppsClosed,
        bool rebootAfterShutdown,
        uint reason);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Shut Windows down; on failure retry once after 10 seconds.</summary>
    /// <returns>True if the shutdown was successfully initiated.</returns>
    public static async Task<bool> ShutdownAsync(FileLog log)
    {
        if (TryShutdown(log))
            return true;

        log.Warning("Shutdown failed; retrying once in 10 s.");
        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return TryShutdown(log);
    }

    private static bool TryShutdown(FileLog log)
    {
        try
        {
            EnableShutdownPrivilege(log);
            if (!InitiateSystemShutdownExW(
                    null, ShutdownMessage, 0,
                    forceAppsClosed: true, rebootAfterShutdown: false, ShutdownReason))
            {
                log.Error($"InitiateSystemShutdownExW failed with win32 error {Marshal.GetLastWin32Error()}.");
                return false;
            }

            log.Info("System shutdown initiated.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Exception initiating shutdown: {ex}");
            return false;
        }
    }

    private static void EnableShutdownPrivilege(FileLog log)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");

        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed");

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed");

            // AdjustTokenPrivileges can succeed without assigning the privilege.
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotAllAssigned)
                log.Warning("SeShutdownPrivilege was not assigned to this process token.");
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
