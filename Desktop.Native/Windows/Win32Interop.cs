using Remotely.Shared.Models;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using static Remotely.Desktop.Native.Windows.ADVAPI32;
using static Remotely.Desktop.Native.Windows.User32;

namespace Remotely.Desktop.Native.Windows;

// TODO: Use https://github.com/microsoft/CsWin32 for all p/invokes.
public class Win32Interop
{
    public static List<WindowsSession> GetActiveSessions()
    {
        var sessions = new List<WindowsSession>();
        var consoleSessionId = Kernel32.WTSGetActiveConsoleSessionId();
        sessions.Add(new WindowsSession()
        {
            Id = consoleSessionId,
            Type = WindowsSessionType.Console,
            Name = "Console",
            Username = GetUsernameFromSessionId(consoleSessionId)
        });

        nint ppSessionInfo = nint.Zero;
        var count = 0;
        var enumSessionResult = WTSAPI32.WTSEnumerateSessions(WTSAPI32.WTS_CURRENT_SERVER_HANDLE, 0, 1, ref ppSessionInfo, ref count);
        var dataSize = Marshal.SizeOf(typeof(WTSAPI32.WTS_SESSION_INFO));
        var current = ppSessionInfo;

        if (enumSessionResult != 0)
        {
            for (int i = 0; i < count; i++)
            {
                var wtsInfo = Marshal.PtrToStructure(current, typeof(WTSAPI32.WTS_SESSION_INFO));
                if (wtsInfo is null)
                {
                    continue;
                }
                var sessionInfo = (WTSAPI32.WTS_SESSION_INFO)wtsInfo;
                current += dataSize;
                if (sessionInfo.State == WTSAPI32.WTS_CONNECTSTATE_CLASS.WTSActive && sessionInfo.SessionID != consoleSessionId)
                {

                    sessions.Add(new WindowsSession()
                    {
                        Id = sessionInfo.SessionID,
                        Name = sessionInfo.pWinStationName,
                        Type = WindowsSessionType.RDP,
                        Username = GetUsernameFromSessionId(sessionInfo.SessionID)
                    });
                }
            }
        }

        return sessions;
    }

    public static string GetCommandLine()
    {
        var commandLinePtr = Kernel32.GetCommandLine();
        return Marshal.PtrToStringAuto(commandLinePtr) ?? string.Empty;
    }

    public static bool GetCurrentDesktop([NotNullWhen(true)] out string? desktopName)
    {
        desktopName = null;
        var inputDesktop = OpenInputDesktop();
        try
        {
            if (TryGetDesktopName(inputDesktop, out desktopName))
            {
                return true;
            }

            return false;
        }
        finally
        {
            CloseDesktop(inputDesktop);
        }
    }



    public static string GetUsernameFromSessionId(uint sessionId)
    {
        var username = string.Empty;

        if (WTSAPI32.WTSQuerySessionInformation(nint.Zero, sessionId, WTSAPI32.WTS_INFO_CLASS.WTSUserName, out var buffer, out var strLen) && strLen > 1)
        {
            username = Marshal.PtrToStringAnsi(buffer);
            WTSAPI32.WTSFreeMemory(buffer);
        }

        return username ?? string.Empty;
    }

    public static nint OpenInputDesktop()
    {
        return User32.OpenInputDesktop(0, true, ACCESS_MASK.GENERIC_ALL);
    }

    public static bool CreateInteractiveSystemProcess(
        string commandLine,
        int targetSessionId,
        bool hiddenWindow,
        out PROCESS_INFORMATION procInfo)
    {
        uint winlogonPid = 0;
        var hUserTokenDup = nint.Zero;
        var hPToken = nint.Zero;
        var hProcess = nint.Zero;

        procInfo = new PROCESS_INFORMATION();

        var dwSessionId = ResolveWindowsSession(targetSessionId);

        // Obtain the process ID of the winlogon process that is running within the currently active session.
        var processes = Process.GetProcessesByName("winlogon");
        foreach (Process p in processes)
        {
            if ((uint)p.SessionId == dwSessionId)
            {
                winlogonPid = (uint)p.Id;
            }
        }

        // Obtain a handle to the winlogon process.
        hProcess = Kernel32.OpenProcess(MAXIMUM_ALLOWED, false, winlogonPid);

        // Obtain a handle to the access token of the winlogon process.
        if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE, ref hPToken))
        {
            Kernel32.CloseHandle(hProcess);
            return false;
        }

        // Security attibute structure used in DuplicateTokenEx and CreateProcessAsUser.
        var sa = new SECURITY_ATTRIBUTES();
        sa.Length = Marshal.SizeOf(sa);

        // Copy the access token of the winlogon process; the newly created token will be a primary token.
        if (!DuplicateTokenEx(hPToken, MAXIMUM_ALLOWED, ref sa, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, out hUserTokenDup))
        {
            Kernel32.CloseHandle(hProcess);
            Kernel32.CloseHandle(hPToken);
            return false;
        }

        // By default, CreateProcessAsUser creates a process on a non-interactive window station, meaning
        // the window station has a desktop that is invisible and the process is incapable of receiving
        // user input. To remedy this we set the lpDesktop parameter to indicate we want to enable user 
        // interaction with the new process.
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        si.lpDesktop = @"winsta0\" + ResolveDesktopName(dwSessionId);

        // Flags that specify the priority and creation method of the process.
        uint dwCreationFlags;
        if (hiddenWindow)
        {
            dwCreationFlags = NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = 0;
        }
        else
        {
            dwCreationFlags = NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;
        }

        // Create a new process in the current user's logon session.
        var result = CreateProcessAsUser(
            hUserTokenDup,
            null,
            commandLine,
            ref sa,
            ref sa,
            false,
            dwCreationFlags,
            nint.Zero,
            null,
            ref si,
            out procInfo);

        // Invalidate the handles.
        Kernel32.CloseHandle(hProcess);
        Kernel32.CloseHandle(hPToken);
        Kernel32.CloseHandle(hUserTokenDup);

        return result;
    }

    public static string ResolveDesktopName(uint targetSessionId)
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var logonUiPath = Path.Combine(winDir, "System32", "LogonUI.exe");
        var consentPath = Path.Combine(winDir, "System32", "consent.exe");

        var isLogonScreenVisible = Process
            .GetProcessesByName("LogonUI")
            .Any(x => x.SessionId == targetSessionId && x.MainModule?.FileName.Equals(logonUiPath, StringComparison.OrdinalIgnoreCase) == true);

        var isSecureDesktopVisible = Process
            .GetProcessesByName("consent")
            .Any(x => x.SessionId == targetSessionId && x.MainModule?.FileName.Equals(consentPath, StringComparison.OrdinalIgnoreCase) == true);

        if (isLogonScreenVisible || isSecureDesktopVisible)
        {
            return "Winlogon";
        }

        return "Default";
    }

    public static uint ResolveWindowsSession(int targetSessionId)
    {
        var activeSessions = GetActiveSessions();
        if (activeSessions.Any(x => x.Id == targetSessionId))
        {
            // If exact match is found, return that session.
            return (uint)targetSessionId;
        }

        if (Shlwapi.IsOS(OsType.OS_ANYSERVER))
        {
            // If Windows Server, default to console session.
            return Kernel32.WTSGetActiveConsoleSessionId();
        }

        // If consumer version and there's an RDP session active, return that.
        if (activeSessions.Find(x => x.Type == WindowsSessionType.RDP) is { } rdSession)
        {
            return rdSession.Id;
        }

        // Otherwise, return the console session.
        return Kernel32.WTSGetActiveConsoleSessionId();
    }

    public static void SetMonitorState(MonitorState state)
    {
        SendMessage(0xFFFF, 0x112, 0xF170, (int)state);
    }

    public static MessageBoxResult ShowMessageBox(nint owner,
        string message,
        string caption,
        MessageBoxType messageBoxType)
    {
        return (MessageBoxResult)MessageBox(owner, message, caption, (long)messageBoxType);
    }

    [SupportedOSPlatform("windows")]
    public static bool RelaunchElevated(string username, string domain, string password, string commandLineArgs, out PROCESS_INFORMATION procInfo, out string errorMessage, out int win32ErrorCode)
    {
        procInfo = new PROCESS_INFORMATION();
        errorMessage = string.Empty;
        win32ErrorCode = 0;

        var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var commandLine = $"\"{exePath}\" {commandLineArgs}";

        // Grant the target user's SID access to the current window station and desktop
        // before launching. A new logon session created by CreateProcessWithLogonW gets a
        // fresh logon SID that is not in winsta0's DACL, so USER32/GDI32 DLL init would
        // fail with 0xC0000142 without this step.
        GrantWindowStationAndDesktopAccess(username, domain);

        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        // Leave lpDesktop null so the child inherits the caller's desktop; the ACL grant
        // above ensures the new session token is allowed to connect to it.

        // CreateProcessWithLogonW requires no elevated caller privileges, unlike LogonUser
        // (which requires SE_TCB_NAME) or CreateProcessAsUser (which requires
        // SE_ASSIGNPRIMARYTOKEN_NAME). For admin accounts under UAC it creates a
        // high-integrity process — the same mechanism used by runas.exe.
        var result = CreateProcessWithLogonW(
            username,
            domain,
            password,
            LOGON_WITH_PROFILE,
            null,
            commandLine,
            NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT,
            nint.Zero,
            null,
            ref si,
            out procInfo);

        if (!result)
        {
            win32ErrorCode = Marshal.GetLastWin32Error();
            errorMessage = $"Win32 error {win32ErrorCode}: {new System.ComponentModel.Win32Exception(win32ErrorCode).Message}";
            return false;
        }

        // CreateProcessWithLogonW returning true only means the process was created; it can
        // still die immediately (e.g. 0xC0000142 if it failed to attach to the desktop). Give
        // it a moment and confirm it's still alive before the caller shuts itself down.
        var waitResult = Kernel32.WaitForSingleObject(procInfo.hProcess, 2000);
        if (waitResult == Kernel32.WAIT_OBJECT_0)
        {
            var exitCode = 0u;
            Kernel32.GetExitCodeProcess(procInfo.hProcess, out exitCode);
            errorMessage = $"Elevated process exited immediately with code 0x{exitCode:X}.";
            return false;
        }

        return true;
    }

    // The logon ladder, tried in order. BATCH/NETWORK_CLEARTEXT of a domain admin return a full,
    // unfiltered, already-elevated primary token directly; INTERACTIVE only yields a usable token
    // via its linked token, which requires SeTcbPrivilege and so mostly fails for a normal caller.
    private static readonly LOGON_TYPE[] _elevationLadder =
    {
        LOGON_TYPE.LOGON32_LOGON_BATCH,
        LOGON_TYPE.LOGON32_LOGON_NETWORK_CLEARTEXT,
        LOGON_TYPE.LOGON32_LOGON_INTERACTIVE,
    };

    /// <summary>
    /// Relaunches the process as a high-integrity process running as the given admin user.
    /// UAC token filtering is applied to *interactive* logons, so a LOGON32_LOGON_BATCH (or
    /// NETWORK_CLEARTEXT) logon of a domain account returns the full, unfiltered, already-elevated
    /// primary token directly - no linked token, no SeTcbPrivilege required. This ladder tries
    /// those first, verifying elevation on every candidate, and only falls back to the
    /// interactive-logon + linked-token dance (which requires SeTcbPrivilege to yield a usable
    /// token and so mostly exists for local admin accounts on machines where it happens to work).
    /// Per-rung token acquisition and diagnosis is shared with <see cref="RunElevationSelfTest"/>
    /// via <see cref="EvaluateRung"/> so the diagnostic spike and the real relaunch cannot drift.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool RelaunchElevatedHighIntegrity(
        string username,
        string domain,
        string password,
        string commandLineArgs,
        out PROCESS_INFORMATION procInfo,
        out string errorMessage,
        out int win32ErrorCode,
        out string diagnosticLog)
    {
        procInfo = new PROCESS_INFORMATION();
        errorMessage = string.Empty;
        win32ErrorCode = 0;

        var attempts = new List<string>();

        foreach (var logonType in _elevationLadder)
        {
            var rung = EvaluateRung(logonType, username, domain, password, out var logonToken, out var linkedToken);
            try
            {
                attempts.Add(rung.Format());

                if (rung.UsableElevatedToken == nint.Zero)
                {
                    win32ErrorCode = rung.Win32ErrorCode;
                    continue;
                }

                if (LaunchWithElevatedToken(
                        rung.UsableElevatedToken,
                        rung.UsableLevel,
                        username,
                        domain,
                        commandLineArgs,
                        out procInfo,
                        out var launchError,
                        out var launchCode))
                {
                    diagnosticLog = $"Elevation succeeded via {logonType}." +
                        (attempts.Count > 1 ? $" Earlier rungs: {string.Join(" | ", attempts.Take(attempts.Count - 1))}" : string.Empty);
                    errorMessage = string.Empty;
                    win32ErrorCode = 0;
                    return true;
                }

                win32ErrorCode = launchCode;
                attempts[^1] += $" -> launch failed: {launchError}";
            }
            finally
            {
                if (logonToken != nint.Zero) Kernel32.CloseHandle(logonToken);
                if (linkedToken != nint.Zero) Kernel32.CloseHandle(linkedToken);
            }
        }

        diagnosticLog = string.Join(" | ", attempts);
        errorMessage = attempts.Count == 0 ? "No elevation ladder rungs were attempted." : attempts[^1];
        return false;
    }

    /// <summary>
    /// Runs the full logon ladder for DIAGNOSTICS ONLY: it acquires and inspects the token for
    /// every rung and reports what it found, but never launches a process. Backs the hidden
    /// --elevation-selftest switch so the ladder can be validated on a target machine without side
    /// effects. Shares <see cref="EvaluateRung"/> with the real relaunch path.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string RunElevationSelfTest(string username, string domain, string password)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Elevation self-test (diagnostic only - no process is launched) ===");
        sb.AppendLine($"User: {domain}\\{username}");
        sb.AppendLine(DescribeCallerPrivileges());
        sb.AppendLine("Ladder:");

        foreach (var logonType in _elevationLadder)
        {
            var rung = EvaluateRung(logonType, username, domain, password, out var logonToken, out var linkedToken);
            try
            {
                sb.Append("  ").AppendLine(rung.Format());
                if (rung.UsableElevatedToken != nint.Zero)
                {
                    sb.AppendLine($"    => WOULD LAUNCH via CreateProcessAsUser (duplicate level {rung.UsableLevel}).");
                }
            }
            finally
            {
                if (logonToken != nint.Zero) Kernel32.CloseHandle(logonToken);
                if (linkedToken != nint.Zero) Kernel32.CloseHandle(linkedToken);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Per-rung diagnosis and token acquisition, WITHOUT launching anything. Shared by the real
    /// relaunch and the self-test so both see identical behaviour.
    /// </summary>
    /// <param name="logonToken">The raw token from LogonUser (nint.Zero if LogonUser failed). The
    /// caller owns it and must close it.</param>
    /// <param name="linkedToken">The interactive rung's linked token (nint.Zero otherwise). The
    /// caller owns it and must close it.</param>
    [SupportedOSPlatform("windows")]
    private static RungDiagnostics EvaluateRung(
        LOGON_TYPE logonType,
        string username,
        string domain,
        string password,
        out nint logonToken,
        out nint linkedToken)
    {
        logonToken = nint.Zero;
        linkedToken = nint.Zero;
        var diag = new RungDiagnostics { LogonType = logonType };

        if (!LogonUser(username, domain, password, (int)logonType, (int)LOGON_PROVIDER.LOGON32_PROVIDER_DEFAULT, out logonToken))
        {
            diag.Win32ErrorCode = Marshal.GetLastWin32Error();
            return diag;
        }
        diag.LogonUserSucceeded = true;

        if (logonType == LOGON_TYPE.LOGON32_LOGON_INTERACTIVE)
        {
            diag.IsLinkedTokenRung = true;

            var size = Marshal.SizeOf<TOKEN_LINKED_TOKEN>();
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(logonToken, SECUR32.TOKEN_INFORMATION_CLASS.TokenLinkedToken, buf, (uint)size, out _))
                {
                    diag.Win32ErrorCode = Marshal.GetLastWin32Error();
                    diag.FailureReason = $"GetTokenInformation(TokenLinkedToken) failed. Win32 error {diag.Win32ErrorCode}: {new System.ComponentModel.Win32Exception(diag.Win32ErrorCode).Message}";
                    return diag;
                }
                linkedToken = Marshal.PtrToStructure<TOKEN_LINKED_TOKEN>(buf).LinkedToken;
            }
            finally { Marshal.FreeHGlobal(buf); }

            diag.LinkedTokenRetrieved = true;
            diag.TokenIsElevated = IsTokenElevated(linkedToken, out _);
            diag.TokenElevationType = GetTokenElevationType(linkedToken);
            // The linked token's actual impersonation level: a caller without SeTcbPrivilege only
            // gets it at SecurityIdentification, which cannot be duplicated up to a usable token.
            diag.LinkedTokenLevel = GetImpersonationLevel(linkedToken, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification);

            if (!diag.TokenIsElevated)
            {
                diag.FailureReason = "linked token is not elevated";
                return diag;
            }
            if (diag.LinkedTokenLevel < SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation)
            {
                diag.FailureReason = $"linked token impersonation level is only {diag.LinkedTokenLevel} (needs SeTcbPrivilege for SecurityImpersonation)";
                return diag;
            }

            diag.UsableElevatedToken = linkedToken;
            diag.UsableLevel = diag.LinkedTokenLevel;
            return diag;
        }

        // BATCH / NETWORK_CLEARTEXT: for a domain admin the logon token itself is already a full,
        // unfiltered, elevated primary token. Verify anyway - this is what catches a local account
        // silently coming back UAC-filtered instead of failing outright.
        diag.TokenIsElevated = IsTokenElevated(logonToken, out var elevationDetail);
        diag.TokenElevationType = GetTokenElevationType(logonToken);
        if (!diag.TokenIsElevated)
        {
            diag.FailureReason = $"token not elevated ({elevationDetail})";
            return diag;
        }

        diag.UsableElevatedToken = logonToken;
        diag.UsableLevel = SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation;
        return diag;
    }

    private sealed class RungDiagnostics
    {
        public LOGON_TYPE LogonType;
        public bool LogonUserSucceeded;
        public int Win32ErrorCode;
        public bool TokenIsElevated;
        public string TokenElevationType = "not checked";
        public bool IsLinkedTokenRung;
        public bool LinkedTokenRetrieved;
        public SECURITY_IMPERSONATION_LEVEL LinkedTokenLevel;
        public nint UsableElevatedToken;
        public SECURITY_IMPERSONATION_LEVEL UsableLevel = SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation;
        public string? FailureReason;

        public string Format()
        {
            var sb = new StringBuilder();
            sb.Append(LogonType).Append(": ");

            if (!LogonUserSucceeded)
            {
                sb.Append($"LogonUser failed (Win32 {Win32ErrorCode}: {new System.ComponentModel.Win32Exception(Win32ErrorCode).Message})");
                return sb.ToString();
            }

            sb.Append("LogonUser OK");
            if (IsLinkedTokenRung)
            {
                if (!LinkedTokenRetrieved)
                {
                    sb.Append($"; {FailureReason}");
                    return sb.ToString();
                }
                sb.Append($"; linked token elevated={TokenIsElevated}, elevationType={TokenElevationType}, impersonationLevel={LinkedTokenLevel}");
            }
            else
            {
                sb.Append($"; token elevated={TokenIsElevated}, elevationType={TokenElevationType}");
            }

            if (UsableElevatedToken != nint.Zero)
            {
                sb.Append(" -> usable elevated token");
            }
            else if (FailureReason != null)
            {
                sb.Append($" -> unusable: {FailureReason}");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Reports whether the current (base) process token holds the privileges relevant to the two
    /// process-creation strategies. CreateProcessAsUser needs SeAssignPrimaryTokenPrivilege +
    /// SeIncreaseQuotaPrivilege (which the impersonated admin token supplies); this documents why
    /// impersonation is required and why the old CreateProcessWithTokenW path was wrong.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string DescribeCallerPrivileges()
    {
        var wanted = new[]
        {
            "SeImpersonatePrivilege",
            "SeAssignPrimaryTokenPrivilege",
            "SeIncreaseQuotaPrivilege",
            "SeTcbPrivilege",
        };

        var hToken = nint.Zero;
        try
        {
            if (!OpenProcessToken(Kernel32.GetCurrentProcess(), TOKEN_QUERY, ref hToken))
            {
                return $"Caller (base process) privileges: <OpenProcessToken failed, Win32 {Marshal.GetLastWin32Error()}>";
            }

            var held = GetTokenPrivilegeLuids(hToken);
            var parts = wanted.Select(name =>
                LookupPrivilegeValue(null, name, out var luid) && held.Contains((luid.LowPart, luid.HighPart))
                    ? $"{name}=present"
                    : $"{name}=absent");

            return "Caller (base process) privileges: " + string.Join(", ", parts);
        }
        finally
        {
            if (hToken != nint.Zero) Kernel32.CloseHandle(hToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<(uint Low, int High)> GetTokenPrivilegeLuids(nint token)
    {
        var result = new HashSet<(uint, int)>();

        GetTokenInformation(token, SECUR32.TOKEN_INFORMATION_CLASS.TokenPrivileges, nint.Zero, 0, out var needed);
        if (needed == 0) return result;

        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, SECUR32.TOKEN_INFORMATION_CLASS.TokenPrivileges, buf, needed, out _))
            {
                return result;
            }

            // TOKEN_PRIVILEGES { DWORD PrivilegeCount; LUID_AND_ATTRIBUTES Privileges[]; }
            // LUID_AND_ATTRIBUTES = LUID { DWORD LowPart; LONG HighPart; } + DWORD Attributes = 12 bytes.
            var count = Marshal.ReadInt32(buf);
            var offset = 4;
            for (var i = 0; i < count; i++)
            {
                var low = (uint)Marshal.ReadInt32(buf, offset);
                var high = Marshal.ReadInt32(buf, offset + 4);
                result.Add((low, high));
                offset += 12;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static string GetTokenElevationType(nint token)
    {
        var buf = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(token, SECUR32.TOKEN_INFORMATION_CLASS.TokenElevationType, buf, sizeof(int), out _))
            {
                return $"unknown (Win32 {Marshal.GetLastWin32Error()})";
            }

            return Marshal.ReadInt32(buf) switch
            {
                1 => "Default(1)",
                2 => "Full(2)",
                3 => "Limited(3)",
                var v => $"({v})",
            };
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Reads a token's logon SID (the unique S-1-5-5-X-Y granted to its logon session). The child
    /// process created from this token gets the same logon SID, which must be in the window-station
    /// and desktop DACL or USER32/GDI32 init fails.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? TryGetLogonSid(nint token)
    {
        GetTokenInformation(token, SECUR32.TOKEN_INFORMATION_CLASS.TokenLogonSid, nint.Zero, 0, out var needed);
        if (needed == 0) return null;

        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, SECUR32.TOKEN_INFORMATION_CLASS.TokenLogonSid, buf, needed, out _))
            {
                return null;
            }

            // TOKEN_GROUPS { DWORD GroupCount; SID_AND_ATTRIBUTES Groups[]; }. The array begins
            // after GroupCount plus pointer-alignment padding, so at offset nint.Size. Groups[0].Sid
            // is the first field of the first SID_AND_ATTRIBUTES.
            var count = Marshal.ReadInt32(buf);
            if (count < 1) return null;

            var sidPtr = Marshal.ReadIntPtr(buf + nint.Size);
            return sidPtr == nint.Zero ? null : new SecurityIdentifier(sidPtr);
        }
        catch
        {
            return null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [SupportedOSPlatform("windows")]
    private static bool LaunchWithElevatedToken(
        nint elevatedToken,
        SECURITY_IMPERSONATION_LEVEL duplicateLevel,
        string username,
        string domain,
        string commandLineArgs,
        out PROCESS_INFORMATION procInfo,
        out string errorMessage,
        out int win32ErrorCode)
    {
        procInfo = new PROCESS_INFORMATION();
        errorMessage = string.Empty;
        win32ErrorCode = 0;

        var primaryToken = nint.Zero;
        var impersonating = false;

        try
        {
            var sa = new SECURITY_ATTRIBUTES();
            sa.Length = Marshal.SizeOf(sa);

            if (!DuplicateTokenEx(elevatedToken, MAXIMUM_ALLOWED, ref sa, duplicateLevel, TOKEN_TYPE.TokenPrimary, out primaryToken))
            {
                win32ErrorCode = Marshal.GetLastWin32Error();
                errorMessage = $"DuplicateTokenEx failed. Win32 error {win32ErrorCode}: {new System.ComponentModel.Win32Exception(win32ErrorCode).Message}";
                return false;
            }

            // Impersonate the elevated admin token so THIS thread carries the admin's
            // SeAssignPrimaryTokenPrivilege + SeIncreaseQuotaPrivilege, which CreateProcessAsUser
            // requires. NOTE: the previous implementation used CreateProcessWithTokenW here, which
            // failed with 1346 ERROR_BAD_IMPERSONATION_LEVEL because that API launches via the
            // Secondary Logon service (seclogon) over RPC and rejects the call while the calling
            // thread is impersonating. CreateProcessAsUser does not route through seclogon, works
            // under impersonation, and is the same primitive CreateInteractiveSystemProcess uses.
            if (!ImpersonateLoggedOnUser(elevatedToken))
            {
                win32ErrorCode = Marshal.GetLastWin32Error();
                errorMessage = $"ImpersonateLoggedOnUser failed. Win32 error {win32ErrorCode}: {new System.ComponentModel.Win32Exception(win32ErrorCode).Message}";
                return false;
            }
            impersonating = true;

            // Grant both the account SID and the elevated token's logon SID access to the window
            // station/desktop; the child runs in that token's fresh logon session.
            GrantWindowStationAndDesktopAccess(username, domain, elevatedToken);

            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            var commandLine = $"\"{exePath}\" {commandLineArgs}";

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            // Leave lpDesktop null so the child inherits the caller's desktop; the ACL grant
            // above ensures the new session token is allowed to connect to it.

            var result = CreateProcessAsUser(
                primaryToken,
                null,
                commandLine,
                ref sa,
                ref sa,
                false,
                NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT,
                nint.Zero,
                null,
                ref si,
                out procInfo);

            if (!result)
            {
                win32ErrorCode = Marshal.GetLastWin32Error();
                errorMessage = $"CreateProcessAsUser failed. Win32 error {win32ErrorCode}: {new System.ComponentModel.Win32Exception(win32ErrorCode).Message}";
                return false;
            }

            // CreateProcessAsUser returning true only means the process was created; it can
            // still die immediately. Give it a moment and confirm it's still alive before the
            // caller shuts itself down.
            var waitResult = Kernel32.WaitForSingleObject(procInfo.hProcess, 2000);
            if (waitResult == Kernel32.WAIT_OBJECT_0)
            {
                var exitCode = 0u;
                Kernel32.GetExitCodeProcess(procInfo.hProcess, out exitCode);
                errorMessage = $"Elevated process exited immediately with code 0x{exitCode:X}.";
                return false;
            }

            return true;
        }
        finally
        {
            if (impersonating)
            {
                RevertToSelf();
            }

            if (primaryToken != nint.Zero) Kernel32.CloseHandle(primaryToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTokenElevated(nint token, out string detail)
    {
        detail = string.Empty;
        var size = Marshal.SizeOf<TOKEN_ELEVATION>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, SECUR32.TOKEN_INFORMATION_CLASS.TokenElevation, buf, (uint)size, out _))
            {
                var code = Marshal.GetLastWin32Error();
                detail = $"GetTokenInformation(TokenElevation) failed, Win32 error {code}: {new System.ComponentModel.Win32Exception(code).Message}";
                return false;
            }

            var elevation = Marshal.PtrToStructure<TOKEN_ELEVATION>(buf);
            if (elevation.TokenIsElevated == 0)
            {
                detail = "TokenIsElevated = 0";
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SECURITY_IMPERSONATION_LEVEL GetImpersonationLevel(nint token, SECURITY_IMPERSONATION_LEVEL fallback)
    {
        var size = sizeof(int);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, SECUR32.TOKEN_INFORMATION_CLASS.TokenImpersonationLevel, buf, (uint)size, out _))
            {
                return fallback;
            }

            return (SECURITY_IMPERSONATION_LEVEL)Marshal.ReadInt32(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Enables the named privilege (e.g. SeDebugPrivilege) in the current process's token.
    /// Privileges like SeDebugPrivilege are present-but-disabled by default even in an elevated
    /// admin token, and must be explicitly enabled before use.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool EnablePrivilege(string privilegeName)
    {
        var hToken = nint.Zero;
        try
        {
            var hProcess = Kernel32.GetCurrentProcess();
            if (!OpenProcessToken(hProcess, TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, ref hToken))
            {
                return false;
            }

            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new[]
                {
                    new TOKEN_PRIVILEGES.LUID_AND_ATTRIBUTES
                    {
                        Luid = luid,
                        Attributes = SE_PRIVILEGE_ENABLED
                    }
                }
            };
            var prevState = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new TOKEN_PRIVILEGES.LUID_AND_ATTRIBUTES[1]
            };

            if (!AdjustTokenPrivileges(hToken, false, ref tp, (uint)Marshal.SizeOf<TOKEN_PRIVILEGES>(), ref prevState, out _))
            {
                return false;
            }

            // AdjustTokenPrivileges can return true while silently not assigning the privilege
            // (e.g. ERROR_NOT_ALL_ASSIGNED); that only surfaces via GetLastError, not the return value.
            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            if (hToken != nint.Zero) Kernel32.CloseHandle(hToken);
        }
    }

    public static bool SwitchToInputDesktop()
    {
        try
        {
            var inputDesktop = OpenInputDesktop();

            try
            {
                if (inputDesktop == nint.Zero)
                {
                    return false;
                }

                return SetThreadDesktop(inputDesktop);
            }
            finally
            {
                CloseDesktop(inputDesktop);
            }
        }
        catch
        {
            return false;
        }
    }

    public static void SetConsoleWindowVisibility(bool isVisible)
    {
        var handle = Kernel32.GetConsoleWindow();

        if (isVisible)
        {
            ShowWindow(handle, (int)SW.SW_SHOW);
        }
        else
        {
            ShowWindow(handle, (int)SW.SW_HIDE);
        }

        Kernel32.CloseHandle(handle);
    }

    [SupportedOSPlatform("windows")]
    private static void GrantWindowStationAndDesktopAccess(string username, string domain, nint token = 0)
    {
        try
        {
            const int WINSTA_ALL_ACCESS  = 0x37F;
            const int DESKTOP_ALL_ACCESS = 0x1FF;
            const uint DACL_INFO         = 0x4; // DACL_SECURITY_INFORMATION

            var sids = new List<SecurityIdentifier>();

            SecurityIdentifier? accountSid = null;
            foreach (var account in new[] { TryMakeAccount(username), TryMakeAccount(domain, username) })
            {
                try { accountSid = (SecurityIdentifier?)account?.Translate(typeof(SecurityIdentifier)); }
                catch { /* try next */ }
                if (accountSid != null) break;
            }
            if (accountSid != null) sids.Add(accountSid);

            // A freshly created logon session is granted a unique *logon SID* (S-1-5-5-X-Y) that is
            // distinct from the account SID and is not in winsta0's DACL by default. USER32/GDI32
            // init in the child fails (0xC0000142) unless that logon SID is granted too, so add it
            // when we have the launching token. (Not available for the CreateProcessWithLogonW
            // fallback, which creates its logon session internally after this point.)
            if (token != nint.Zero)
            {
                var logonSid = TryGetLogonSid(token);
                if (logonSid != null) sids.Add(logonSid);
            }

            if (sids.Count == 0) return;

            var winsta = GetProcessWindowStation();
            var desktop = GetThreadDesktop(Kernel32.GetCurrentThreadId());
            foreach (var sid in sids)
            {
                GrantObjectAccess(winsta, sid, WINSTA_ALL_ACCESS, DACL_INFO);
                GrantObjectAccess(desktop, sid, DESKTOP_ALL_ACCESS, DACL_INFO);
            }
        }
        catch { /* best-effort; the launch call will surface any resulting error */ }
    }

    [SupportedOSPlatform("windows")]
    private static NTAccount? TryMakeAccount(string user, string? domain = null) =>
        string.IsNullOrWhiteSpace(user) ? null :
        domain is null ? new NTAccount(user) : new NTAccount(domain, user);

    [SupportedOSPlatform("windows")]
    private static void GrantObjectAccess(nint hObject, SecurityIdentifier sid, int accessMask, uint securityInfo)
    {
        GetUserObjectSecurity(hObject, ref securityInfo, nint.Zero, 0, out var needed);
        if (needed == 0) return;

        var sdBuf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetUserObjectSecurity(hObject, ref securityInfo, sdBuf, needed, out _)) return;

            var sdBytes = new byte[needed];
            Marshal.Copy(sdBuf, sdBytes, 0, (int)needed);

            var sd   = new RawSecurityDescriptor(sdBytes, 0);
            var dacl = sd.DiscretionaryAcl ?? new RawAcl(RawAcl.AclRevision, 4);
            dacl.InsertAce(dacl.Count,
                new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, accessMask, sid, false, null));
            sd.DiscretionaryAcl = dacl;

            var newSd = new byte[sd.BinaryLength];
            sd.GetBinaryForm(newSd, 0);
            SetUserObjectSecurity(hObject, ref securityInfo, newSd);
        }
        finally { Marshal.FreeHGlobal(sdBuf); }
    }

    public static bool TryGetDesktopName(nint desktopHandle, [NotNullWhen(true)] out string? desktopName)
    {
        var deskBytes = new byte[256];
        if (!GetUserObjectInformationW(desktopHandle, UOI_NAME, deskBytes, 256, out uint lenNeeded))
        {
            desktopName = string.Empty;
            return false;
        }

        desktopName = Encoding.Unicode
            .GetString(deskBytes.Take((int)lenNeeded).ToArray())
            .Replace("\0", "");

        return true;
    }
}
