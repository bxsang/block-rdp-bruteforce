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
        if (!TryResolveInteractiveSession(out var sessionId, out var userToken, out var resolveError))
        {
            return resolveError;
        }

        IntPtr elevatedToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;

        try
        {
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
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    NativeMethods.TOKEN_TYPE.TokenPrimary,
                    out primaryToken))
            {
                return LaunchResult.Failed(
                    $"DuplicateTokenEx failed (Win32 {Marshal.GetLastWin32Error()})");
            }

            // The linked elevated token retrieved via TokenLinkedToken is created at
            // logon and can carry session id 0. Without re-stamping it here, the new
            // process is created in session 0 (the service session) and the .NET host
            // dies before reaching managed code, since WinForms can't bind to the
            // session-0 desktop. Pin it to the resolved interactive session.
            var sidBuf = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(sidBuf, (int)sessionId);
                if (!NativeMethods.SetTokenInformation(
                        primaryToken,
                        NativeMethods.TOKEN_INFORMATION_CLASS.TokenSessionId,
                        sidBuf,
                        sizeof(uint)))
                {
                    _log.LogWarning(
                        "SetTokenInformation(TokenSessionId={Session}) failed (Win32 {Err}); process may launch in wrong session",
                        sessionId, Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sidBuf);
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

            // No CREATE_NEW_CONSOLE: WinForms doesn't need a console, and allocating
            // one cross-session requires csrss cooperation that fails when the
            // primary token's session id was re-stamped — producing
            // STATUS_DLL_INIT_FAILED (0xC0000142) before managed code can run.
            var ok = NativeMethods.CreateProcessAsUser(
                primaryToken,
                applicationPath,
                cmdBuffer,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CREATE_UNICODE_ENVIRONMENT,
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

    // Picks the session a user-visible UI should run in. We prefer the physical
    // console (the local desktop), but fall back to enumerating Terminal Services
    // sessions when the console has no logged-in user — common on RDP-only
    // headless boxes (e.g. Server VMs, dev machines accessed remotely).
    private bool TryResolveInteractiveSession(
        out uint sessionId, out IntPtr userToken, out LaunchResult error)
    {
        sessionId = 0;
        userToken = IntPtr.Zero;
        error = LaunchResult.NoActiveSession();

        var consoleSession = NativeMethods.WTSGetActiveConsoleSessionId();
        if (consoleSession != 0xFFFFFFFFu &&
            NativeMethods.WTSQueryUserToken(consoleSession, out userToken))
        {
            sessionId = consoleSession;
            return true;
        }

        var consoleErr = consoleSession == 0xFFFFFFFFu
            ? "no console session"
            : $"Win32 {Marshal.GetLastWin32Error()}";

        if (TryFindActiveTsSession(out sessionId, out userToken, out var enumErr))
        {
            _log.LogInformation(
                "Console session unavailable ({Console}); using interactive TS session {Session}",
                consoleErr, sessionId);
            return true;
        }

        error = enumErr == null
            ? LaunchResult.NoActiveSession()
            : LaunchResult.Failed($"No interactive session found ({consoleErr}; {enumErr})");
        return false;
    }

    private static bool TryFindActiveTsSession(
        out uint sessionId, out IntPtr userToken, out string? error)
    {
        sessionId = 0;
        userToken = IntPtr.Zero;
        error = null;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WTSEnumerateSessions(
                    IntPtr.Zero, 0, 1, out buffer, out var count))
            {
                error = $"WTSEnumerateSessions failed (Win32 {Marshal.GetLastWin32Error()})";
                return false;
            }

            var entrySize = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(
                    IntPtr.Add(buffer, i * entrySize));

                if (entry.State != NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive) continue;
                if (entry.SessionId == 0) continue; // services session

                if (NativeMethods.WTSQueryUserToken((uint)entry.SessionId, out userToken))
                {
                    sessionId = (uint)entry.SessionId;
                    return true;
                }
            }

            error = "no active TS session with a queryable user token";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try { NativeMethods.WTSFreeMemory(buffer); } catch { }
            }
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
            TokenSessionId = 12,
            TokenElevationType = 18,
            TokenLinkedToken = 19,
        }

        public enum TOKEN_ELEVATION_TYPE
        {
            TokenElevationTypeDefault = 1,
            TokenElevationTypeFull = 2,
            TokenElevationTypeLimited = 3,
        }

        public enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive = 0,
            WTSConnected = 1,
            WTSConnectQuery = 2,
            WTSShadow = 3,
            WTSDisconnected = 4,
            WTSIdle = 5,
            WTSListen = 6,
            WTSReset = 7,
            WTSDown = 8,
            WTSInit = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WTS_SESSION_INFO
        {
            public int SessionId;
            [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
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

        [DllImport("Wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WTSEnumerateSessions(
            IntPtr serverHandle,
            uint reserved,
            uint version,
            out IntPtr ppSessionInfo,
            out int sessionCount);

        [DllImport("Wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr memory);

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

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetTokenInformation(
            IntPtr token,
            TOKEN_INFORMATION_CLASS infoClass,
            IntPtr buffer,
            int bufferSize);

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
