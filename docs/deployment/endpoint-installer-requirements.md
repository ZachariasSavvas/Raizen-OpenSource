# Raizen Endpoint Installer — Requirements

**Document type:** Installer functional specification
**Package format:** MSI (Windows Installer)
**Target platform:** Windows 10 / 11, x64
**Components installed:** Raizen Endpoint Service + Raizen Tray App + Shell Extensions

---

## 1. Prerequisites

The installer must verify the following before proceeding. If any check fails, display a clear error and abort.

| Requirement | Minimum version | Check method |
|---|---|---|
| Windows OS | Windows 10 1903 (build 18362) or later | `VersionNT >= 1903` |
| Architecture | x64 only | `VersionNT64` |
| .NET Runtime | .NET 8.0 Desktop Runtime | Registry: `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App` |
| Administrator rights | Required | `Privileged` property |
| Network connectivity | API server reachable | Custom action (ping `ServerUrl` before install completes) |

The .NET 8 Desktop Runtime must be installed first. If absent, the installer should offer to download it (link to official Microsoft download) or bundle it as a merge module.

---

## 2. Installer Screens (UI Flow)

### 2.1 Welcome Screen
- Product name, version, brief description
- "Next" / "Cancel"

### 2.2 License Agreement
- Display EULA
- Accept checkbox required to proceed

### 2.3 Server Configuration *(custom UI page)*
Collect the following fields. Validate before allowing "Next":

| Field | Label | Validation | Default |
|---|---|---|---|
| `ServerUrl` | Raizen Server URL | Must begin with `https://` or `http://`; URL must be reachable | *(empty)* |
| `TlsPinThumbprint` | TLS Certificate Thumbprint (optional) | Empty or 64 hex chars (SHA-256) | *(empty)* |

Display a **"Test Connection"** button that performs a lightweight HTTP GET to `{ServerUrl}/health` (or equivalent) and shows success/failure inline.

### 2.4 Ready to Install
- Summary of install path, service name, server URL
- "Install" / "Back" / "Cancel"

### 2.5 Installing (progress bar)
- Show step names: Copying files → Configuring service → Registering context menus → Finalising

### 2.6 Completion Screen
- Show generated **Machine ID** and **API Key** in a copyable text box
- Prominently display next-step instructions:
  > Register this endpoint on the Raizen admin portal before the service will receive any tasks.
  > Navigate to **Endpoints → Register** and enter the values above.
- "Finish" button

---

## 3. Files Installed

### 3.1 Directory layout

```
%ProgramFiles%\Raizen\
  Service\
    Raizen.Endpoint.Service.exe   (and all .dll dependencies)
  Tray\
    Raizen.Endpoint.Tray.exe      (and all .dll dependencies)

%ProgramData%\Raizen\
  raizen-config.json              (written by installer; see §4)
  ApprovedScripts\                (empty; SYSTEM-only write access)
  Logs\                           (empty; SYSTEM-only write access)
```

### 3.2 ACLs applied

| Path | Principal | Rights | Inherited |
|---|---|---|---|
| `%ProgramData%\Raizen\` | NT AUTHORITY\SYSTEM | FullControl | Yes |
| `%ProgramData%\Raizen\` | BUILTIN\Administrators | FullControl | Yes |
| `%ProgramData%\Raizen\` | BUILTIN\Users | ReadAndExecute | Yes |
| `%ProgramData%\Raizen\ApprovedScripts\` | NT AUTHORITY\SYSTEM | FullControl | Yes |
| `%ProgramData%\Raizen\ApprovedScripts\` | BUILTIN\Administrators | FullControl | Yes |
| `%ProgramData%\Raizen\ApprovedScripts\` | BUILTIN\Users | **No access** | No |
| `%ProgramData%\Raizen\Logs\` | NT AUTHORITY\SYSTEM | FullControl | Yes |
| `%ProgramData%\Raizen\Logs\` | BUILTIN\Administrators | ReadAndExecute | Yes |

---

## 4. Configuration File Written

The installer writes `%ProgramData%\Raizen\raizen-config.json`:

```json
{
  "ServerUrl":                "<value from installer UI>",
  "TlsPinThumbprint":         "<value from installer UI, or empty string>",
  "MachineId":                "<read from HKLM\\SOFTWARE\\Microsoft\\Cryptography\\MachineGuid>",
  "ApiKey":                   "<48-byte cryptographically random value, base64url-encoded>",
  "PollIntervalSeconds":      30,
  "HeartbeatIntervalSeconds": 300,
  "HttpProxy":                null,
  "HttpTimeoutSeconds":       30
}
```

**`MachineId`** — read from the Windows registry (`MachineGuid`). This value is stable across reinstalls on the same hardware.
**`ApiKey`** — generated fresh on every new install. On upgrade (same `MachineId`), keep the existing key to avoid breaking the server registration.

---

## 5. Windows Service

| Property | Value |
|---|---|
| Service name | `RaizenEndpoint` |
| Display name | Raizen Elevation Service |
| Description | Brokered JIT elevation agent. Executes approved actions as SYSTEM. |
| Binary path | `"%ProgramFiles%\Raizen\Service\Raizen.Endpoint.Service.exe" --config="%ProgramData%\Raizen\raizen-config.json"` |
| Account | NT AUTHORITY\SYSTEM (LocalSystem) |
| Start type | Automatic |
| Failure recovery | Restart after 5 s (1st), 10 s (2nd), 30 s (subsequent); reset after 86 400 s |

The installer must start the service immediately after installation. If the service fails to start, show a warning but do not roll back (the service will start on next boot or after the endpoint is registered).

---

## 6. Tray App Auto-Start

```
Key:   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
Value: RaizenTray
Data:  "%ProgramFiles%\Raizen\Tray\Raizen.Endpoint.Tray.exe" --config="%ProgramData%\Raizen\raizen-config.json"
```

This launches the tray app for all users on login. The tray app runs as the logged-in (non-elevated) user.

---

## 7. Shell Context Menu Registration

All entries written to `HKEY_LOCAL_MACHINE\SOFTWARE\Classes` (system-wide, visible to all users).

### 7.1 Executable files (`.exe`, `.msi`, `.msc`)
Classes: `exefile`, `Msi.Package`, `Microsoft.Management.Console`

**Raizen** *(cascading submenu)*
- Request to Run as Admin → `"<TrayExe>" --run-as-admin="%1" --config="<ConfigPath>"`
- Request File Transfer... → `"<TrayExe>" --file-transfer="%1" --config="<ConfigPath>"`
- Request File Permissions... → `"<TrayExe>" --file-permissions="%1" --config="<ConfigPath>"`

### 7.2 All other files
Class: `*`

**Raizen**
- Request File Transfer... → `"<TrayExe>" --file-transfer="%1" --config="<ConfigPath>"`
- Request File Permissions... → `"<TrayExe>" --file-permissions="%1" --config="<ConfigPath>"`

### 7.3 Folders (right-click on a folder)
Class: `Directory`

**Raizen**
- Request File Permissions... → `"<TrayExe>" --file-permissions="%1" --config="<ConfigPath>"`

### 7.4 Folder background (right-click inside a folder)
Class: `Directory\Background`

**Raizen**
- Request File Permissions... → `"<TrayExe>" --file-permissions="%V" --config="<ConfigPath>"`

*(Note: `%V` instead of `%1` for `Directory\Background` — `%V` resolves to the folder path.)*

---

## 8. Upgrade Behaviour

| Scenario | Expected behaviour |
|---|---|
| Same version already installed | Prompt "already installed — repair or remove?" |
| Older version installed | Silent in-place upgrade; stop service → replace binaries → restart service; preserve existing `raizen-config.json` |
| Newer version installed | Warn and abort |
| Config file already present | Keep existing file; do **not** regenerate `ApiKey` or `MachineId` |

---

## 9. Uninstall Behaviour

On uninstall (via Add/Remove Programs or `msiexec /x`):

1. Stop and delete the `RaizenEndpoint` service
2. Remove `RaizenTray` autorun registry value
3. Remove all `HKLM\SOFTWARE\Classes\*\shell\Raizen` context menu entries
4. Remove binaries from `%ProgramFiles%\Raizen\`
5. **Prompt** whether to remove `%ProgramData%\Raizen\` (config, logs, scripts)
   - Default: **No** (preserve data for re-install)
   - If yes: delete entire folder tree

---

## 10. Logging During Install

The installer must write a log to `%TEMP%\Raizen-Setup-<timestamp>.log` for diagnostics. Include:
- All custom action exit codes
- Service start result
- Registry write results
