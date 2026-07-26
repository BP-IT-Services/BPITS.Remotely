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

    /// <summary>
    /// Relaunches the process as a high-integrity process running as the given admin user.
    /// UAC token filtering is applied to *interactive* logons, so a LOGON32_LOGON_BATCH (or
    /// NETWORK_CLEARTEXT) logon of a domain account returns the full, unfiltered, already-elevated
    /// primary token directly - no linked token, no SeTcbPrivilege required. This ladder tries
    /// those first, verifying elevation on every candidate, and only falls back to the
    /// interactive-logon + linked-token dance (which requires SeTcbPrivilege to yield a usable
    /// token and so mostly exists for local admin accounts on machines where it happens to work).
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
        var ladder = new[]
        {
            LOGON_TYPE.LOGON32_LOGON_BATCH,
            LOGON_TYPE.LOGON32_LOGON_NETWORK_CLEARTEXT,
            LOGON_TYPE.LOGON32_LOGON_INTERACTIVE,
        };

        foreach (var logonType in ladder)
        {
            var token = nint.Zero;
            try
            {
                if (!LogonUser(username, domain, password, (int)logonType, (int)LOGON_PROVIDER.LOGON32_PROVIDER_DEFAULT, out token))
                {
                    win32ErrorCode = Marshal.GetLastWin32Error();
                    attempts.Add($"{logonType}: LogonUser failed (Win32 {win32ErrorCode}: {new System.ComponentModel.Win32Exception(win32ErrorCode).Message})");
                    continue;
                }

                bool launched;
                string launchError;
                int launchCode;

                if (logonType == LOGON_TYPE.LOGON32_LOGON_INTERACTIVE)
                {
                    launched = TryElevateFromLinkedToken(username, domain, token, commandLineArgs, out procInfo, out launchError, out launchCode);
                }
                else
                {
                    // Batch/network-cleartext logons of a domain account are NOT UAC-filtered,
                    // but verify anyway - this check is what catches a local (non-domain) account
                    // silently coming back filtered instead of failing outright.
                    if (!IsTokenElevated(token, out var elevationDetail))
                    {
                        attempts.Add($"{logonType}: token not elevated ({elevationDetail})");
                        continue;
                    }

                    launched = LaunchWithElevatedToken(token, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, username, domain, commandLineArgs, out procInfo, out launchError, out launchCode);
                }

                if (launched)
                {
                    diagnosticLog = $"Elevation succeeded via {logonType}." +
                        (attempts.Count > 0 ? $" Earlier rungs failed: {string.Join(" | ", attempts)}" : string.Empty);
                    errorMessage = string.Empty;
                    win32ErrorCode = 0;
                    return true;
                }

                win32ErrorCode = launchCode;
                attempts.Add($"{logonType}: {launchError}");
            }
            finally
            {
                if (token != nint.Zero) Kernel32.CloseHandle(token);
            }
        }

        diagnosticLog = string.Join(" | ", attempts);
        errorMessage = attempts.Count == 0 ? "No elevation ladder rungs were attempted." : attempts[^1];
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryElevateFromLinkedToken(
        string username,
        string domain,
        nint filteredToken,
        string commandLineArgs,
        out PROCESS_INFORMATION procInfo,
        out string errorMessage,
        out int win32ErrorCode)
    {
        procInfo = new PROCESS_INFORMATION();
        errorMessage = string.Empty;
        win32ErrorCode = 0;

        var elevatedToken = nint.Zero;
        var linkedTokenSize = Marshal.SizeOf<TOKEN_LINKED_TOKEN>();
        var linkedTokenBuf = Marshal.AllocHGlobal(linkedTokenSize);
        try
        {
            if (!GetTokenInformation(filteredToken, SECUR32.TOKEN_INFORMATION_CLASS.TokenLinkedToken, linkedTokenBuf, (uint)linkedTokenSize, out _))
            {
                win32ErrorCode = Marshal.GetLastWin32Error();
                errorMessage = $"GetTokenInformation(TokenLinkedToken) failed. Win32 error {win32ErrorCode}: {new System.ComponentModel.Win32Exception(win32ErrorCode).Message}";
                return false;
            }

            var linkedToken = Marshal.PtrToStructure<TOKEN_LINKED_TOKEN>(linkedTokenBuf);
            elevatedToken = linkedToken.LinkedToken;

            if (!IsTokenElevated(elevatedToken, out var elevationDetail))
            {
                errorMessage = $"Linked token is not elevated ({elevationDetail}).";
                return false;
            }

            // Duplicate at the linked token's actual impersonation level, rather than assuming
            // SecurityImpersonation, so a caller without SeTcbPrivilege (who only gets an
            // identification-level linked token) fails cleanly here instead of with the
            // misleading ERROR_BAD_IMPERSONATION_LEVEL (1346) from DuplicateTokenEx.
            var actualLevel = GetImpersonationLevel(elevatedToken, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification);
            if (actualLevel < SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation)
            {
                errorMessage = $"Linked token impersonation level is only {actualLevel}; cannot create a process from it.";
                return false;
            }

            return LaunchWithElevatedToken(elevatedToken, actualLevel, username, domain, commandLineArgs, out procInfo, out errorMessage, out win32ErrorCode);
        }
        finally
        {
            Marshal.FreeHGlobal(linkedTokenBuf);
            if (elevatedToken != nint.Zero) Kernel32.CloseHandle(elevatedToken);
        }
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

            // Impersonating the elevated token on this thread is what grants the calling
            // process the SeImpersonatePrivilege usage that CreateProcessWithTokenW requires -
            // this is what makes the sequence work when launched from a standard-user process.
            if (!ImpersonateLoggedOnUser(elevatedToken))
            {
                win32ErrorCode = Marshal.GetLastWin32Error();
                errorMessage = $"ImpersonateLoggedOnUser failed. Win32 error {win32ErrorCode}: {new System.ComponentModel.Win32Exception(win32ErrorCode).Message}";
                return false;
            }
            impersonating = true;

            GrantWindowStationAndDesktopAccess(username, domain);

            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            var commandLine = $"\"{exePath}\" {commandLineArgs}";

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            // Leave lpDesktop null so the child inherits the caller's desktop; the ACL grant
            // above ensures the new session token is allowed to connect to it.

            var result = CreateProcessWithTokenW(
                primaryToken,
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
                errorMessage = $"CreateProcessWithTokenW failed. Win32 error {win32ErrorCode}: {new System.ComponentModel.Win32Exception(win32ErrorCode).Message}";
                return false;
            }

            // CreateProcessWithTokenW returning true only means the process was created; it can
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
    private static void GrantWindowStationAndDesktopAccess(string username, string domain)
    {
        try
        {
            SecurityIdentifier? sid = null;
            foreach (var account in new[] { TryMakeAccount(username), TryMakeAccount(domain, username) })
            {
                try { sid = (SecurityIdentifier?)account?.Translate(typeof(SecurityIdentifier)); }
                catch { /* try next */ }
                if (sid != null) break;
            }

            if (sid == null) return;

            const int WINSTA_ALL_ACCESS  = 0x37F;
            const int DESKTOP_ALL_ACCESS = 0x1FF;
            const uint DACL_INFO         = 0x4; // DACL_SECURITY_INFORMATION

            GrantObjectAccess(GetProcessWindowStation(), sid, WINSTA_ALL_ACCESS, DACL_INFO);
            GrantObjectAccess(GetThreadDesktop(Kernel32.GetCurrentThreadId()), sid, DESKTOP_ALL_ACCESS, DACL_INFO);
        }
        catch { /* best-effort; CreateProcessWithLogonW will surface any resulting error */ }
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
