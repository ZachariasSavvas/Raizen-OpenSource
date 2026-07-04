namespace Raizen.Server.Web.Security;

public sealed record LolbasEntry(
    string Name,
    string Description,
    string[] Capabilities,
    string[] MitreAttack
);

/// <summary>
/// Curated subset of the LOLBAS project (https://lolbas-project.github.io/).
/// Keyed by lowercase filename (no path) for fast lookup.
/// </summary>
public static class LolbasDatabase
{
    private static readonly Dictionary<string, LolbasEntry> _entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["certutil.exe"] = new("certutil.exe",
            "Certificate management tool built into Windows.",
            ["Download files from the internet", "Decode / encode Base64 data", "Install rogue certificates", "Bypass application whitelisting"],
            ["T1105", "T1140", "T1553.004"]),

        ["mshta.exe"] = new("mshta.exe",
            "Microsoft HTML Application host — runs .hta files.",
            ["Execute arbitrary VBScript / JScript", "Bypass AppLocker and Script Block Logging", "Establish persistence via registry run keys", "Download and execute remote payloads"],
            ["T1218.005", "T1059.005", "T1547.001"]),

        ["regsvr32.exe"] = new("regsvr32.exe",
            "Registers / unregisters COM objects.",
            ["Execute DLLs and remote scriptlets (Squiblydoo)", "Bypass AppLocker", "Download and run remote COM objects", "Proxy execution to evade defences"],
            ["T1218.010", "T1059"]),

        ["rundll32.exe"] = new("rundll32.exe",
            "Loads and calls a DLL export.",
            ["Execute arbitrary DLL code", "Bypass application whitelisting", "Download and run remote DLLs", "Proxy execution"],
            ["T1218.011"]),

        ["powershell.exe"] = new("powershell.exe",
            "Windows PowerShell scripting engine.",
            ["Execute arbitrary code and scripts", "Download files and payloads", "Bypass execution policy", "Disable security tools", "Credential harvesting", "Lateral movement via WinRM/WMI"],
            ["T1059.001", "T1105", "T1562", "T1003"]),

        ["powershell_ise.exe"] = new("powershell_ise.exe",
            "PowerShell Integrated Scripting Environment.",
            ["Execute arbitrary PowerShell scripts", "Bypass execution policy"],
            ["T1059.001"]),

        ["cmd.exe"] = new("cmd.exe",
            "Windows Command Processor.",
            ["Execute arbitrary commands and batch scripts", "Spawn child processes to evade detection", "Manipulate files and registry"],
            ["T1059.003"]),

        ["wscript.exe"] = new("wscript.exe",
            "Windows Script Host — runs VBScript / JScript files.",
            ["Execute arbitrary scripts", "Download and run remote payloads", "Establish persistence", "Bypass application whitelisting"],
            ["T1059.005", "T1059.007", "T1547.001"]),

        ["cscript.exe"] = new("cscript.exe",
            "Console-mode Windows Script Host.",
            ["Execute arbitrary VBScript / JScript", "Download payloads", "Bypass application whitelisting"],
            ["T1059.005", "T1059.007"]),

        ["wmic.exe"] = new("wmic.exe",
            "WMI command-line interface.",
            ["Execute processes locally and remotely", "Lateral movement", "Enumerate system information", "Establish WMI event subscriptions for persistence"],
            ["T1047", "T1546.003", "T1021"]),

        ["msiexec.exe"] = new("msiexec.exe",
            "Windows Installer — installs MSI packages.",
            ["Execute remote MSI payloads over HTTP", "Bypass application whitelisting", "Install malicious software silently"],
            ["T1218.007"]),

        ["bitsadmin.exe"] = new("bitsadmin.exe",
            "Background Intelligent Transfer Service (BITS) admin tool.",
            ["Download files from the internet", "Upload files", "Establish BITS job persistence", "Execute commands on job completion"],
            ["T1197", "T1105"]),

        ["cmstp.exe"] = new("cmstp.exe",
            "Microsoft Connection Manager Profile Installer.",
            ["Execute arbitrary code via malicious INF files", "Bypass AppLocker and UAC", "Download remote payloads"],
            ["T1218.003"]),

        ["regasm.exe"] = new("regasm.exe",
            "Registers .NET assemblies for COM interop.",
            ["Execute arbitrary .NET code", "Bypass AppLocker", "Proxy execution"],
            ["T1218.009"]),

        ["regsvcs.exe"] = new("regsvcs.exe",
            "Registers .NET Component Services.",
            ["Execute arbitrary .NET code", "Bypass AppLocker", "Proxy execution"],
            ["T1218.009"]),

        ["installutil.exe"] = new("installutil.exe",
            ".NET installer utility.",
            ["Execute arbitrary .NET assemblies", "Bypass AppLocker and application whitelisting"],
            ["T1218.004"]),

        ["msbuild.exe"] = new("msbuild.exe",
            "Microsoft Build Engine.",
            ["Execute arbitrary C# / VB.NET code inline in project files", "Bypass AppLocker"],
            ["T1127.001"]),

        ["csc.exe"] = new("csc.exe",
            "C# compiler.",
            ["Compile and execute arbitrary C# code on the fly", "Bypass application whitelisting"],
            ["T1127"]),

        ["vbc.exe"] = new("vbc.exe",
            "Visual Basic compiler.",
            ["Compile and execute arbitrary VB.NET code on the fly", "Bypass application whitelisting"],
            ["T1127"]),

        ["ftp.exe"] = new("ftp.exe",
            "Built-in FTP client.",
            ["Download files from remote servers", "Exfiltrate data"],
            ["T1105"]),

        ["curl.exe"] = new("curl.exe",
            "Command-line HTTP/S transfer tool (built-in since Windows 10 1803).",
            ["Download arbitrary files and payloads", "Exfiltrate data via HTTP/S"],
            ["T1105"]),

        ["expand.exe"] = new("expand.exe",
            "Expands compressed cabinet (.cab) files.",
            ["Download and extract remote .cab payloads", "Bypass content filtering"],
            ["T1105"]),

        ["extrac32.exe"] = new("extrac32.exe",
            "CAB extraction tool for Internet Explorer.",
            ["Extract files from .cab archives", "Copy arbitrary files"],
            ["T1105"]),

        ["hh.exe"] = new("hh.exe",
            "HTML Help executable — opens .chm files.",
            ["Execute arbitrary HTML / JavaScript inside .chm files", "Bypass application whitelisting"],
            ["T1218.001"]),

        ["ieexec.exe"] = new("ieexec.exe",
            "Internet Explorer executable loader.",
            ["Download and execute remote applications"],
            ["T1218"]),

        ["infdefaultinstall.exe"] = new("infdefaultinstall.exe",
            "Processes INF setup scripts.",
            ["Execute arbitrary INF scripts", "Bypass AppLocker"],
            ["T1218"]),

        ["makecab.exe"] = new("makecab.exe",
            "Creates cabinet (.cab) archive files.",
            ["Package and exfiltrate files", "Compress data for exfiltration"],
            ["T1560"]),

        ["mavinject.exe"] = new("mavinject.exe",
            "Microsoft Application Virtualization injector.",
            ["Inject arbitrary DLLs into running processes"],
            ["T1055.001"]),

        ["microsoft.workflow.compiler.exe"] = new("microsoft.workflow.compiler.exe",
            "Windows Workflow Foundation compiler.",
            ["Compile and execute arbitrary C# code", "Bypass AppLocker and WDAC"],
            ["T1127"]),

        ["msdeploy.exe"] = new("msdeploy.exe",
            "Microsoft Web Deploy tool.",
            ["Download and execute remote content"],
            ["T1105"]),

        ["msdt.exe"] = new("msdt.exe",
            "Microsoft Support Diagnostic Tool.",
            ["Execute arbitrary code via protocol handlers (Follina / CVE-2022-30190)", "Remote code execution"],
            ["T1218", "T1203"]),

        ["netsh.exe"] = new("netsh.exe",
            "Network configuration command-line tool.",
            ["Establish persistence via helper DLLs", "Modify firewall rules to allow inbound connections", "Port forwarding / proxying"],
            ["T1546.007", "T1562.004"]),

        ["pcalua.exe"] = new("pcalua.exe",
            "Program Compatibility Assistant.",
            ["Execute arbitrary commands and applications", "Bypass UAC"],
            ["T1218"]),

        ["presentationhost.exe"] = new("presentationhost.exe",
            "WPF XAML browser application host.",
            ["Execute arbitrary XAML browser applications", "Bypass application whitelisting"],
            ["T1218"]),

        ["reg.exe"] = new("reg.exe",
            "Windows Registry command-line editor.",
            ["Add / modify registry run keys for persistence", "Export credentials from SAM hive", "Disable security features via registry"],
            ["T1547.001", "T1003.002", "T1562"]),

        ["regini.exe"] = new("regini.exe",
            "Modifies registry permissions and values via script.",
            ["Modify registry for persistence or defence evasion"],
            ["T1547.001"]),

        ["replace.exe"] = new("replace.exe",
            "Replaces files.",
            ["Copy files to arbitrary locations", "Overwrite security-critical files"],
            ["T1105"]),

        ["rpcping.exe"] = new("rpcping.exe",
            "Validates RPC connections.",
            ["Steal Net-NTLMv2 credentials via forced RPC authentication"],
            ["T1187"]),

        ["runscripthelper.exe"] = new("runscripthelper.exe",
            "Runs scripts for Windows Sandbox.",
            ["Execute arbitrary PowerShell scripts", "Bypass application whitelisting"],
            ["T1218"]),

        ["sc.exe"] = new("sc.exe",
            "Service Control Manager command-line interface.",
            ["Create or modify Windows services for persistence", "Start / stop security services", "Execute code as SYSTEM via service creation"],
            ["T1543.003", "T1562.001"]),

        ["scriptrunner.exe"] = new("scriptrunner.exe",
            "Runs scripts for App-V packages.",
            ["Execute arbitrary scripts", "Bypass AppLocker"],
            ["T1218"]),

        ["schtasks.exe"] = new("schtasks.exe",
            "Creates and manages scheduled tasks.",
            ["Establish persistence via scheduled tasks", "Execute code as SYSTEM", "Lateral movement via remote task creation"],
            ["T1053.005"]),

        ["syncappvpublishingserver.exe"] = new("syncappvpublishingserver.exe",
            "App-V publishing synchronisation tool.",
            ["Execute arbitrary PowerShell commands", "Bypass PowerShell execution policy"],
            ["T1059.001"]),

        ["te.exe"] = new("te.exe",
            "Test Authoring and Execution Framework binary.",
            ["Execute arbitrary DLLs and test binaries", "Bypass application whitelisting"],
            ["T1218"]),

        ["tracker.exe"] = new("tracker.exe",
            "Microsoft Build file tracker.",
            ["Inject arbitrary DLLs into processes"],
            ["T1055.001"]),

        ["wab.exe"] = new("wab.exe",
            "Windows Address Book application.",
            ["Execute arbitrary DLLs", "Proxy execution"],
            ["T1218"]),

        ["winrm.cmd"] = new("winrm.cmd",
            "Windows Remote Management command-line tool.",
            ["Remote command execution", "Lateral movement"],
            ["T1021.006"]),

        ["xwizard.exe"] = new("xwizard.exe",
            "Extensible Wizard host process.",
            ["Execute arbitrary COM objects / DLLs", "Bypass AppLocker"],
            ["T1218"]),

        ["eudcedit.exe"] = new("eudcedit.exe",
            "Character editor application.",
            ["Load arbitrary DLLs via DLL side-loading"],
            ["T1574.002"]),

        ["diskshadow.exe"] = new("diskshadow.exe",
            "Volume Shadow Copy Service (VSS) management tool.",
            ["Execute arbitrary commands via script mode", "Access VSS copies of locked files (e.g. NTDS.dit)", "Bypass application whitelisting"],
            ["T1218", "T1003.003"]),

        ["esentutl.exe"] = new("esentutl.exe",
            "Extensible Storage Engine utility.",
            ["Copy locked files (SAM, NTDS.dit)", "Download files via WebDAV"],
            ["T1003", "T1105"]),

        ["findstr.exe"] = new("findstr.exe",
            "Searches files for text patterns.",
            ["Download files from WebDAV / UNC paths"],
            ["T1105"]),

        ["forfiles.exe"] = new("forfiles.exe",
            "Batch processing command.",
            ["Execute arbitrary commands indirectly", "Bypass application whitelisting"],
            ["T1218"]),

        ["nltest.exe"] = new("nltest.exe",
            "Network location test / domain trust enumeration tool.",
            ["Enumerate domain controllers and trust relationships", "Network reconnaissance"],
            ["T1482", "T1016"]),

        ["ntdsutil.exe"] = new("ntdsutil.exe",
            "Active Directory database utility.",
            ["Dump Active Directory credentials (NTDS.dit)", "Install AD DS components"],
            ["T1003.003"]),

        ["pcwrun.exe"] = new("pcwrun.exe",
            "Program Compatibility Wizard launcher.",
            ["Execute arbitrary applications with argument injection", "Bypass UAC"],
            ["T1218"]),

        ["wuauclt.exe"] = new("wuauclt.exe",
            "Windows Update client.",
            ["Execute arbitrary DLLs via /UpdateDeploymentProvider", "Bypass application whitelisting"],
            ["T1218"]),
    };

    /// <summary>
    /// Returns LOLBAS info if the filename (extracted from path) is a known LOLBAS, otherwise null.
    /// </summary>
    public static LolbasEntry? Check(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        var filename = Path.GetFileName(executablePath);
        return _entries.TryGetValue(filename, out var entry) ? entry : null;
    }
}
