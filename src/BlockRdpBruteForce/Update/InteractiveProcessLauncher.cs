using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Update;

[SupportedOSPlatform("windows")]
public sealed class InteractiveProcessLauncher
{
    private readonly ILogger<InteractiveProcessLauncher> _log;

    public InteractiveProcessLauncher(ILogger<InteractiveProcessLauncher> log)
    {
        _log = log;
    }

    public LaunchResult LaunchAsActiveUser(
        string applicationPath,
        string commandLine,
        bool requestElevation)
    {
        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFFu)
        {
            return LaunchResult.NoActiveSession();
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr elevatedToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;

        try
        {
            if (!NativeMethods.WTSQueryUserToken(sessionId, out userToken))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == NativeMethods.ERROR_NO_TOKEN)
                    return LaunchResult.NoActiveSession();
                return LaunchResult.Failed($"WTSQueryUserToken failed (Win32 {err})");
            }

            var tokenForProcess = userToken;
            var elevated = false;

            if (requestElevation)
            {
                if (TryGetLinkedElevatedToken(userToken, out elevatedToken))
                {
                    tokenForProcess = elevatedToken;
                    elevated = true;
                }
                else
                {
                    _log.LogInformation(
                        "No linked elevated token for active session {Session}; falling back to non-elevated user context",
                        sessionId);
                }
            }

            if (!NativeMethods.DuplicateTokenEx(
                    tokenForProcess,
                    NativeMethods.TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                    NativeMethods.TOKEN_TYPE.TokenPrimary,
                    out primaryToken))
            {
                return LaunchResult.Failed(
                    $"DuplicateTokenEx failed (Win32 {Marshal.GetLastWin32Error()})");
            }

            if (!NativeMethods.CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                environment = IntPtr.Zero; // proceed with caller's env
            }

            var startupInfo = new NativeMethods.STARTUPINFO
            {
                cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };

            // CreateProcessAsUser wants a writable command line buffer.
            var cmd = $"\"{applicationPath}\" {commandLine}".Trim();
            var cmdBuffer = new System.Text.StringBuilder(cmd, cmd.Length + 1);

            var ok = NativeMethods.CreateProcessAsUser(
                primaryToken,
                applicationPath,
                cmdBuffer,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CREATE_UNICODE_ENVIRONMENT | NativeMethods.CREATE_NEW_CONSOLE,
                environment,
                null,
                ref startupInfo,
                out var pi);

            if (!ok)
            {
                return LaunchResult.Failed(
                    $"CreateProcessAsUser failed (Win32 {Marshal.GetLastWin32Error()})");
            }

            try
            {
                NativeMethods.CloseHandle(pi.hThread);
                NativeMethods.CloseHandle(pi.hProcess);
            }
            catch { }

            return LaunchResult.Success((int)pi.dwProcessId, elevated);
        }
        catch (Exception ex)
        {
            return LaunchResult.Failed(ex.Message);
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                try { NativeMethods.DestroyEnvironmentBlock(environment); } catch { }
            }
            SafeClose(primaryToken);
            SafeClose(elevatedToken);
            SafeClose(userToken);
        }
    }

    private static bool TryGetLinkedElevatedToken(IntPtr userToken, out IntPtr elevatedToken)
    {
        elevatedToken = IntPtr.Zero;
        var elevationType = IntPtr.Zero;

        try
        {
            elevationType = Marshal.AllocHGlobal(sizeof(int));
            if (!NativeMethods.GetTokenInformation(
                    userToken,
                    NativeMethods.TOKEN_INFORMATION_CLASS.TokenElevationType,
                    elevationType,
                    sizeof(int),
                    out _))
            {
                return false;
            }

            var elevation = (NativeMethods.TOKEN_ELEVATION_TYPE)Marshal.ReadInt32(elevationType);
            if (elevation != NativeMethods.TOKEN_ELEVATION_TYPE.TokenElevationTypeLimited)
            {
                // Default (no UAC split) or already-full-admin: nothing to upgrade.
                return false;
            }

            var linked = IntPtr.Zero;
            try
            {
                linked = Marshal.AllocHGlobal(IntPtr.Size);
                if (!NativeMethods.GetTokenInformation(
                        userToken,
                        NativeMethods.TOKEN_INFORMATION_CLASS.TokenLinkedToken,
                        linked,
                        IntPtr.Size,
                        out _))
                {
                    return false;
                }

                elevatedToken = Marshal.ReadIntPtr(linked);
                return elevatedToken != IntPtr.Zero;
            }
            finally
            {
                if (linked != IntPtr.Zero) Marshal.FreeHGlobal(linked);
            }
        }
        finally
        {
            if (elevationType != IntPtr.Zero) Marshal.FreeHGlobal(elevationType);
        }
    }

    private static void SafeClose(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { NativeMethods.CloseHandle(handle); } catch { }
    }

    private static class NativeMethods
    {
        public const uint TOKEN_ALL_ACCESS = 0x000F01FF;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint CREATE_NEW_CONSOLE = 0x00000010;
        public const int ERROR_NO_TOKEN = 1008;

        public enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous = 0,
            SecurityIdentification = 1,
            SecurityImpersonation = 2,
            SecurityDelegation = 3,
        }

        public enum TOKEN_TYPE
        {
            TokenPrimary = 1,
            TokenImpersonation = 2,
        }

        public enum TOKEN_INFORMATION_CLASS
        {
            TokenUser = 1,
            TokenElevationType = 18,
            TokenLinkedToken = 19,
        }

        public enum TOKEN_ELEVATION_TYPE
        {
            TokenElevationTypeDefault = 1,
            TokenElevationTypeFull = 2,
            TokenElevationTypeLimited = 3,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
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
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(
            IntPtr existingToken,
            uint desiredAccess,
            IntPtr tokenAttributes,
            SECURITY_IMPERSONATION_LEVEL impersonationLevel,
            TOKEN_TYPE tokenType,
            out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(
            IntPtr token,
            TOKEN_INFORMATION_CLASS infoClass,
            IntPtr buffer,
            int bufferSize,
            out int returnLength);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool CreateEnvironmentBlock(
            out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(
            IntPtr token,
            string? applicationName,
            System.Text.StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

public sealed class LaunchResult
{
    public bool Ok { get; init; }
    public bool FellBackToSystem { get; init; }
    public bool ProcessElevated { get; init; }
    public string? Error { get; init; }
    public int? ProcessId { get; init; }

    public static LaunchResult Success(int pid, bool elevated) =>
        new() { Ok = true, ProcessId = pid, ProcessElevated = elevated };

    public static LaunchResult NoActiveSession() =>
        new() { Ok = false, FellBackToSystem = true, Error = "No active console session" };

    public static LaunchResult Failed(string error) =>
        new() { Ok = false, Error = error };
}
