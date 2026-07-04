# Raizen Endpoint — IT Setup Guide

**Audience:** IT administrators deploying Raizen to managed Windows workstations
**Time required:** ~10 minutes per machine (or automated via Intune/GPO)
**Requires:** Administrator rights on the target machine; Raizen server already running

---

## Overview

The Raizen endpoint consists of two components:

- **Raizen Endpoint Service** — a Windows service running as SYSTEM that executes approved privileged actions
- **Raizen Tray App** — a lightweight WinForms app running as the logged-in user that lets users request elevated actions via right-click context menus

Neither component gives users an elevated shell or persistent admin rights. Every action requires explicit approval from a Raizen admin.

---

## Prerequisites

Before installing on any machine, ensure:

- [ ] Raizen server is deployed and accessible (test: open `https://api.raizen.contoso.com` in a browser — you should see a 404 or API response, not a connection error)
- [ ] Target machine runs Windows 10 (build 18362+) or Windows 11, x64
- [ ] .NET 8 Desktop Runtime is installed on the target machine
  - Check: `dotnet --list-runtimes | findstr Microsoft.WindowsDesktop`
  - Download: https://dotnet.microsoft.com/download/dotnet/8.0 → "Desktop Runtime"
- [ ] You have a local administrator account or are running under a domain admin

---

## Step 1 — Run the Installer

1. Copy `Raizen-Endpoint-Setup-<version>.msi` to the target machine
2. Right-click the MSI → **Run as administrator**
3. Work through the installer wizard:
   - Accept the license agreement
   - On the **Server Configuration** screen, enter:
     - **Raizen Server URL**: `https://api.raizen.contoso.com` *(ask your Raizen administrator)*
     - **TLS Certificate Thumbprint**: leave blank unless your organisation uses certificate pinning
   - Click **Test Connection** and confirm it shows green before proceeding
4. Click **Install**
5. On the **Completion** screen, note the **Machine ID** and **API Key** — you will need these in Step 2

> **Tip — silent install for mass deployment:**
> ```
> msiexec /i Raizen-Endpoint-Setup.msi /qn /l*v C:\Temp\raizen-install.log ^
>   SERVERURL="https://api.raizen.contoso.com"
> ```
> Machine ID and API Key will be written to `C:\ProgramData\Raizen\raizen-config.json` and printed to the install log.

---

## Step 2 — Register the Endpoint on the Server

After installation, the service will start but will not receive any tasks until it is registered.

**Option A — Admin portal (recommended)**

1. Open the Raizen admin portal: `https://admin.raizen.contoso.com`
2. Log in with your admin credentials
3. Navigate to **Endpoints → Register New Endpoint**
4. Enter:
   - **Machine Name**: the computer's hostname (e.g. `DESKTOP-ABC123`)
   - **Machine ID**: copied from the installer completion screen
   - **API Key**: copied from the installer completion screen
5. Click **Register**
6. The endpoint will appear in the Endpoints list with status **Offline** until the service sends its first heartbeat (within 5 minutes)

**Option B — API call (for scripted deployments)**

```powershell
# Run on the target machine after install, or from any machine with admin API access
$config  = Get-Content 'C:\ProgramData\Raizen\raizen-config.json' | ConvertFrom-Json
$headers = @{
    'Authorization'       = 'Bearer <your-admin-token>'
    'X-Raizen-MachineId'  = $config.MachineId
    'X-Raizen-NewApiKey'  = $config.ApiKey
}
$body = @{ MachineName = $env:COMPUTERNAME } | ConvertTo-Json
Invoke-RestMethod -Uri "$($config.ServerUrl)/api/v1/endpoints/register" `
                  -Method POST -Headers $headers -Body $body -ContentType 'application/json'
```

---

## Step 3 — Verify the Installation

### 3.1 Check the service is running
```
services.msc → "Raizen Elevation Service" → Status: Running
```
Or via PowerShell:
```powershell
Get-Service RaizenEndpoint | Select-Object Status, StartType
```
Expected: `Status = Running, StartType = Automatic`

### 3.2 Check the tray app
Log in as a standard user (not the admin account used for installation). Look for the Raizen icon in the system tray (bottom-right). If it is not visible, check the hidden icons area (^ arrow).

### 3.3 Verify context menus
Right-click any file in Windows Explorer. You should see:
```
Raizen ▶
  Request File Transfer...
  Request File Permissions...
```
Right-click an `.exe` file. You should see:
```
Raizen ▶
  Request to Run as Admin
  Request File Transfer...
  Request File Permissions...
```

### 3.4 Check admin portal
Open `https://admin.raizen.contoso.com` → **Endpoints**. The machine should appear with:
- Status: **Online** (green) within 5 minutes of registration
- Last Seen: recent timestamp

---

## Step 4 — Configure Action Definitions (Admin task)

Out of the box, no actions are auto-approved. A Raizen admin must configure which action types are available and who can approve them.

1. Log in to the admin portal
2. Navigate to **Action Definitions**
3. For each action type you want to enable:
   - Set **Approver Group(s)** — AD groups or individual admin accounts who can approve
   - Optionally enable **Auto-Approve** for low-risk actions (e.g. StartService on specific services)
   - Set **Approval Window** — how long an approved action is valid before it expires

---

## Deployed File Locations

| Path | Contents |
|---|---|
| `%ProgramFiles%\Raizen\Service\` | Endpoint service binary and dependencies |
| `%ProgramFiles%\Raizen\Tray\` | Tray app binary and dependencies |
| `%ProgramData%\Raizen\raizen-config.json` | Config file (ServerUrl, ApiKey, MachineId) |
| `%ProgramData%\Raizen\ApprovedScripts\` | Hash-verified scripts (SYSTEM-only write) |
| `%ProgramData%\Raizen\Logs\` | Service logs (rolling daily, 30-day retention) |

---

## Configuration Reference

`%ProgramData%\Raizen\raizen-config.json` — edit with a text editor (admin required):

| Field | Default | Description |
|---|---|---|
| `ServerUrl` | *(set at install)* | Raizen API base URL. Changes take effect on next poll — no service restart needed. |
| `TlsPinThumbprint` | `""` | SHA-256 thumbprint of the server's TLS cert for certificate pinning. Leave blank to trust the system CA store. |
| `MachineId` | *(auto-generated)* | Do not change. Changing this breaks server registration. |
| `ApiKey` | *(auto-generated)* | Do not change. If rotated, re-register the endpoint on the server. |
| `PollIntervalSeconds` | `30` | How often the service checks for approved actions. Minimum: 5. |
| `HeartbeatIntervalSeconds` | `300` | How often the service sends a heartbeat. Minimum: 60. |
| `HttpProxy` | `null` | HTTP proxy URL, e.g. `"http://proxy.contoso.com:8080"`. Set to `null` for direct connection. |
| `HttpTimeoutSeconds` | `30` | HTTP request timeout. Increase on high-latency networks. |

---

## Troubleshooting

### Service won't start
```powershell
# Check the Windows Event Log for details
Get-EventLog -LogName Application -Source 'Raizen*' -Newest 20
# Or check the service log
Get-Content 'C:\ProgramData\Raizen\Logs\service-*.log' -Tail 50
```
Common causes:
- Config file missing or malformed JSON → check `raizen-config.json`
- Server URL unreachable → check firewall/proxy settings
- .NET 8 runtime not installed → install Microsoft.WindowsDesktop.App 8.x

### Tray app not appearing after login
- Check `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` for `RaizenTray` value
- Verify `%ProgramFiles%\Raizen\Tray\Raizen.Endpoint.Tray.exe` exists
- Run the tray app manually from that path to see any error dialogs

### Context menus not showing
- Confirm registry keys exist:
  ```powershell
  Test-Path 'HKLM:\SOFTWARE\Classes\*\shell\Raizen'
  Test-Path 'HKLM:\SOFTWARE\Classes\Directory\shell\Raizen'
  ```
- Restart Explorer: `taskkill /F /IM explorer.exe && start explorer.exe`

### Endpoint shows "Offline" in portal
- Service may not be running — check `services.msc`
- Endpoint may not be registered — re-run Step 2
- Firewall may be blocking outbound HTTPS to the server URL — verify with:
  ```powershell
  Invoke-WebRequest -Uri "https://api.raizen.contoso.com" -UseBasicParsing
  ```

### Requests submitted but never executed
- Check that the endpoint is registered and **enabled** in the admin portal (Endpoints → Enable)
- Check that the action definition is enabled and has an approver configured
- Check service logs for execution errors

---

## Uninstall

**Via Control Panel / Settings:**
Add or Remove Programs → Raizen Endpoint → Uninstall

**Silent:**
```
msiexec /x Raizen-Endpoint-Setup.msi /qn
```

The uninstaller will:
- Stop and remove the Windows service
- Remove the tray autorun registry entry
- Remove all Raizen context menu entries
- Remove binaries from `%ProgramFiles%\Raizen\`
- Prompt whether to remove `%ProgramData%\Raizen\` (logs, config, scripts)

---

## Mass Deployment (Intune / GPO)

### Microsoft Intune
1. Upload `Raizen-Endpoint-Setup.msi` as a Line-of-Business app
2. Set install command: `msiexec /i Raizen-Endpoint-Setup.msi /qn SERVERURL="https://api.raizen.contoso.com"`
3. Set detection rule: Registry key `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{Raizen-Product-Code}` exists
4. Assign to device group

### Group Policy Software Installation
1. Place the MSI on a network share accessible by computer accounts
2. Computer Configuration → Software Settings → Software Installation → New Package
3. Select the MSI; choose "Assigned" deployment
4. The MSI will install on next Group Policy refresh (gpupdate or reboot)

### Post-install endpoint registration at scale
After the MSI is deployed, run this script (e.g. via Intune remediation or GPO startup script) on each machine to auto-register with the server:

```powershell
#Requires -RunAsAdministrator
$config      = Get-Content 'C:\ProgramData\Raizen\raizen-config.json' | ConvertFrom-Json
$adminToken  = 'YOUR_ADMIN_BEARER_TOKEN'  # Store in a secret vault, not plain text in production
$serverUrl   = $config.ServerUrl

try {
    Invoke-RestMethod -Uri "$serverUrl/api/v1/endpoints/register" -Method POST `
        -Headers @{
            'Authorization'      = "Bearer $adminToken"
            'X-Raizen-MachineId' = $config.MachineId
            'X-Raizen-NewApiKey' = $config.ApiKey
        } `
        -Body (@{ MachineName = $env:COMPUTERNAME } | ConvertTo-Json) `
        -ContentType 'application/json'
    Write-Host "Registered $env:COMPUTERNAME successfully."
} catch {
    Write-Warning "Registration failed: $_"
}
```
