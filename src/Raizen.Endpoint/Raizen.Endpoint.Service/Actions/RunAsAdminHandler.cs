using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Launches an approved executable as SYSTEM inside the active user's desktop session.
/// Because the service already runs as SYSTEM, it duplicates its own token, reassigns the
/// token's session ID to the logged-in user's console session, then calls CreateProcessAsUser.
/// The resulting process appears on the user's screen with full SYSTEM / administrator privileges.
///
/// Required parameter:
///   ExecutablePath — Absolute local path to the .exe, .msi, or .msc to launch.
///
/// Security:
///   - Only .exe / .msi / .msc extensions are accepted.
///   - Path must not contain traversal sequences or shell meta-characters.
///   - File must exist at execution time.
///   - No shell (cmd / powershell) is ever invoked.
/// </summary>
public sealed class RunAsAdminHandler(ILogger<RunAsAdminHandler> log) : IActionHandler
{
    // Allow only absolute local paths ending in .exe/.msi/.msc; no traversal, no special chars
    private static readonly Regex SafeExePath = new(
        @"^[A-Za-z]:\\(?!.*\.\.)[^""<>|?*\r\n]+\.(exe|msi|msc)$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    public ActionType HandledType => ActionType.RunAsAdmin;

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var exePath = request.Parameters.GetValueOrDefault("ExecutablePath", "").Trim().Trim('"');

        // Canonicalize to catch .., ., //, etc. — reject if the path changed
        string canonical;
        try { canonical = Path.GetFullPath(exePath); }
        catch
        {
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: "ExecutablePath is not a valid file system path."));
        }

        if (!string.Equals(canonical, exePath, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: "ExecutablePath must be fully qualified with no traversal sequences."));

        exePath = canonical;

        if (!SafeExePath.IsMatch(exePath))
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: $"ExecutablePath '{exePath}' is not a permitted path or extension (.exe/.msi/.msc only)."));

        if (!File.Exists(exePath))
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: $"File not found: {exePath}"));

        try
        {
            var target = BuildLaunchTarget(exePath);
            var pid = LaunchAsSystemInUserSession(target);
            log.LogInformation("[Request:{Id}] Launched '{Exe}' as SYSTEM in user session (PID {Pid}).",
                request.Id, exePath, pid);

            return Task.FromResult(new ActionResult(true,
                ResultMessage: $"Launched '{Path.GetFileName(exePath)}' with elevation (PID {pid})."));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{Id}] RunAsAdmin failed for '{Exe}'.", request.Id, exePath);
            return Task.FromResult(new ActionResult(false, ErrorMessage: "RunAsAdmin operation failed. See endpoint logs for details."));
        }
    }

    /// <summary>
    /// Duplicates the current SYSTEM process token, redirects its session to the active
    /// interactive desktop, then creates the target process there.  The launched process
    /// runs as SYSTEM but is visible on the logged-in user's screen.
    /// </summary>
    internal static LaunchTarget BuildLaunchTarget(string approvedPath)
    {
        var ext = Path.GetExtension(approvedPath).ToLowerInvariant();
        var workingDirectory = Path.GetDirectoryName(approvedPath) ?? Environment.SystemDirectory;

        return ext switch
        {
            ".msi" => CreateSystemToolTarget("msiexec.exe", $"/i \"{approvedPath}\"", workingDirectory),
            ".msc" => CreateSystemToolTarget("mmc.exe", $"\"{approvedPath}\"", workingDirectory),
            _ => new LaunchTarget(approvedPath, null, workingDirectory),
        };
    }

    private static LaunchTarget CreateSystemToolTarget(string toolName, string arguments, string workingDirectory)
    {
        var toolPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), toolName);
        return new LaunchTarget(toolPath, $"\"{toolPath}\" {arguments}", workingDirectory);
    }

    internal static int LaunchAsSystemInUserSession(LaunchTarget target)
    {
        // Identify the active console session (the session with the physical desktop)
        uint sessionId = NativeRunAs.WTSGetActiveConsoleSessionId();

        if (!NativeRunAs.OpenProcessToken(
                NativeRunAs.GetCurrentProcess(),
                NativeRunAs.TOKEN_ALL_ACCESS,
                out var processToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");

        try
        {
            // Duplicate the SYSTEM token as a primary token we can modify
            var sa = new NativeRunAs.SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<NativeRunAs.SECURITY_ATTRIBUTES>()
            };

            if (!NativeRunAs.DuplicateTokenEx(
                    processToken,
                    NativeRunAs.TOKEN_ALL_ACCESS,
                    ref sa,
                    NativeRunAs.SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    NativeRunAs.TOKEN_TYPE.TokenPrimary,
                    out var dupToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");

            try
            {
                // Redirect the token's session to the active user session so the
                // process spawns on WinSta0\Default (the visible desktop)
                uint sid = sessionId;
                if (!NativeRunAs.SetTokenInformation(
                        dupToken,
                        NativeRunAs.TOKEN_INFORMATION_CLASS.TokenSessionId,
                        ref sid,
                        (uint)sizeof(uint)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation (session) failed.");

                var si = new NativeRunAs.STARTUPINFO
                {
                    cb        = (uint)Marshal.SizeOf<NativeRunAs.STARTUPINFO>(),
                    lpDesktop = "WinSta0\\Default",
                };

                if (!NativeRunAs.CreateProcessAsUser(
                        dupToken,
                        target.ApplicationPath,
                        target.CommandLine,
                        IntPtr.Zero, IntPtr.Zero,
                        false,
                        NativeRunAs.CREATE_NEW_CONSOLE | NativeRunAs.CREATE_UNICODE_ENVIRONMENT,
                        IntPtr.Zero,
                        target.WorkingDirectory,
                        ref si,
                        out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");

                var pid = (int)pi.dwProcessId;
                NativeRunAs.CloseHandle(pi.hProcess);
                NativeRunAs.CloseHandle(pi.hThread);
                return pid;
            }
            finally
            {
                NativeRunAs.CloseHandle(dupToken);
            }
        }
        finally
        {
            NativeRunAs.CloseHandle(processToken);
        }
    }
}

internal sealed record LaunchTarget(
    string ApplicationPath,
    string? CommandLine,
    string WorkingDirectory);

/// <summary>Native Win32 declarations for privileged process creation.</summary>
internal static class NativeRunAs
{
    internal const uint TOKEN_ALL_ACCESS           = 0x000F01FF;
    internal const uint CREATE_NEW_CONSOLE         = 0x00000010;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetTokenInformation(
        IntPtr tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        ref uint tokenInformation,
        uint tokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName, string? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    internal enum SECURITY_IMPERSONATION_LEVEL { SecurityImpersonation = 2 }
    internal enum TOKEN_TYPE                   { TokenPrimary = 1 }
    internal enum TOKEN_INFORMATION_CLASS      { TokenSessionId = 12 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        internal uint    nLength;
        internal IntPtr  lpSecurityDescriptor;
        internal bool    bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        internal uint    cb;
        internal string? lpReserved;
        internal string? lpDesktop;
        internal string? lpTitle;
        internal uint    dwX, dwY, dwXSize, dwYSize;
        internal uint    dwXCountChars, dwYCountChars;
        internal uint    dwFillAttribute, dwFlags;
        internal ushort  wShowWindow, cbReserved2;
        internal IntPtr  lpReserved2;
        internal IntPtr  hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        internal IntPtr hProcess, hThread;
        internal uint   dwProcessId, dwThreadId;
    }
}
