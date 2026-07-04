namespace Raizen.Shared.Enums;

/// <summary>
/// Exhaustive list of permitted atomic elevation actions.
/// Adding an action type here requires a corresponding IActionHandler implementation
/// in Raizen.Endpoint.Service.  Shell invocation (cmd, powershell, wscript) is
/// intentionally absent and must never be added.
/// </summary>
public enum ActionType
{
    /// <summary>Install a specific MSI/MSIX package using msiexec.exe APIs.</summary>
    InstallMsi = 10,

    /// <summary>Uninstall a specific MSI package by product code.</summary>
    UninstallMsi = 11,

    /// <summary>Add a domain or local user to a specific local security group.</summary>
    AddLocalGroupMember = 20,

    /// <summary>Remove a domain or local user from a specific local security group.</summary>
    RemoveLocalGroupMember = 21,

    /// <summary>Start a Windows service by name.</summary>
    StartService = 30,

    /// <summary>Stop a Windows service by name.</summary>
    StopService = 31,

    /// <summary>Restart a Windows service by name.</summary>
    RestartService = 32,

    /// <summary>Copy a file to a pre-approved destination path.</summary>
    CopyFile = 40,

    /// <summary>Delete a file from a pre-approved path.</summary>
    DeleteFile = 41,

    /// <summary>Write a specific registry value (DWORD/String) to an allowed key.</summary>
    SetRegistryValue = 50,

    /// <summary>Delete a specific registry value from an allowed key.</summary>
    DeleteRegistryValue = 51,

    /// <summary>
    /// Execute a pre-approved, immutable script identified by SHA-256 hash.
    /// The script must be registered in the action catalog before any request
    /// referencing it can be approved.
    /// </summary>
    RunApprovedScript = 60,

    /// <summary>Create a local Windows user account with a temporary one-use password.</summary>
    CreateLocalUser = 70,

    /// <summary>Disable (lock) a local Windows user account.</summary>
    DisableLocalUser = 71,

    /// <summary>Add a trusted root certificate to the machine store.</summary>
    AddTrustedCertificate = 80,

    /// <summary>Enable or disable a Windows Firewall rule by display name.</summary>
    SetFirewallRule = 90,

    /// <summary>Configure a static IPv4 address or revert to DHCP on a network adapter.</summary>
    SetNetworkConfiguration = 91,

    /// <summary>Create, update, append to, remove an entry from, or delete a system environment variable.</summary>
    SetEnvironmentVariable = 92,

    /// <summary>
    /// Opens the Security/Properties dialog for a file or folder as SYSTEM in the user's session,
    /// allowing the user to change ownership and ACLs via the standard Windows UI.
    /// </summary>
    OpenFileProperties = 95,

    /// <summary>
    /// Launch an approved executable as SYSTEM in the active user's desktop session.
    /// The process appears on the user's screen with full administrator privileges.
    /// Only .exe, .msi, and .msc files are permitted.
    /// </summary>
    RunAsAdmin = 100,
}
