# Raizen Endpoint — Installation Guide (Windows)

> **Target OS:** Windows 10 / 11 or Windows Server 2019 / 2022
> **Required role on endpoint machine:** Local Administrator (install only)
> **Estimated time:** 15–20 minutes per machine

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Build & Distribute the Endpoint Binaries](#2-build--distribute-the-endpoint-binaries)
3. [Run the Installer Script](#3-run-the-installer-script)
4. [Register the Endpoint with the Raizen Server](#4-register-the-endpoint-with-the-raizen-server)
5. [Verify the Service is Running](#5-verify-the-service-is-running)
6. [Deploy the Tray Application to Users](#6-deploy-the-tray-application-to-users)
7. [Changing the Server URL Later](#7-changing-the-server-url-later)
8. [Uninstalling](#8-uninstalling)
9. [Deploying at Scale (Intune / Group Policy)](#9-deploying-at-scale-intune--group-policy)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Prerequisites

### On the endpoint machine

| Requirement | Details |
|---|---|
| .NET 8 Runtime | ASP.NET Core Runtime is **not** needed — only the base runtime |
| Local Admin rights | Required during installation only |
| Outbound HTTPS | Must be able to reach the Raizen server on its configured port (default 443 or 5001) |
| Windows 10 1809+ / Server 2019+ | Earlier versions may lack required .NET 8 APIs |

Install the **.NET 8 Desktop Runtime** (includes WinForms for the tray app):

```
https://dotnet.microsoft.com/en-us/download/dotnet/8.0
→ ".NET Desktop Runtime 8.x" → Windows x64 installer
```

Verify:
```powershell
dotnet --list-runtimes
# Should include: Microsoft.NETCore.App 8.x.x
#                 Microsoft.WindowsDesktop.App 8.x.x
```

---

## 2. Build & Distribute the Endpoint Binaries

This step is done once on your **build machine** (or a CI/CD pipeline). The output is then copied to each endpoint.

### 2.1 Publish the Service

```powershell
cd C:\Build\Raizen

dotnet publish src/Raizen.Endpoint/Raizen.Endpoint.Service `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o C:\Build\publish\service
```

### 2.2 Publish the Tray App

```powershell
dotnet publish src/Raizen.Endpoint/Raizen.Endpoint.Tray `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o C:\Build\publish\tray
```

### 2.3 Prepare the Distribution Package

Create a folder structure to copy to each endpoint:

```
RaizenEndpoint-Setup\
├── publish\
│   ├── service\          ← contents of C:\Build\publish\service
│   └── tray\             ← contents of C:\Build\publish\tray
└── Install-RaizenEndpoint.ps1   ← from scripts\
```

Copy this folder to a network share or deploy via Intune / SCCM.

---

## 3. Run the Installer Script

On the **endpoint machine**, open an elevated PowerShell:

```powershell
# If execution policy blocks scripts, allow for this session only:
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

# Navigate to the setup folder
cd \\fileserver\IT\RaizenEndpoint-Setup

# Run the installer
.\Install-RaizenEndpoint.ps1 -ServerUrl "https://raizen.contoso.com"
```

> Replace `https://raizen.contoso.com` with your actual Raizen server URL (including port if not 443).

### What the installer does

1. Copies service binaries to `C:\Program Files\Raizen\Service\`
2. Copies tray binaries to `C:\Program Files\Raizen\Tray\`
3. Generates a unique API key for this machine
4. Creates the config file at `C:\ProgramData\Raizen\raizen-config.json`
5. Creates the approved scripts directory at `C:\ProgramData\Raizen\ApprovedScripts\`
6. Sets ACLs: SYSTEM + Admins write; Users read (config); SYSTEM + Admins only (scripts)
7. Installs the **RaizenEndpoint** Windows Service (runs as `LocalSystem`)
8. Adds the tray app to **HKLM Run** (launches for all users on sign-in)
9. Starts the service immediately

### Installer output — save this!

At the end of the script, you will see output like this:

```
╔══════════════════════════════════════════════════════════════╗
  ENDPOINT REGISTRATION — save these values now!
╠══════════════════════════════════════════════════════════════╣
  Machine ID : a1b2c3d4-e5f6-7890-ab12-cd34ef567890
  API Key    : Kx9mP2-vQ8rT5nZ1jLhG...
╠══════════════════════════════════════════════════════════════╣
  Register this endpoint on the Raizen server by running:
    POST https://raizen.contoso.com/api/v1/endpoints/register
    Headers:
      Authorization: Bearer <admin-token>
      X-Raizen-MachineId: a1b2c3d4-...
      X-Raizen-NewApiKey: Kx9mP2-vQ8rT5nZ1...
    Body: { "MachineName": "DESKTOP-ABC123" }
╚══════════════════════════════════════════════════════════════╝
```

**Copy the Machine ID and API Key to a notepad** — you'll need them in the next step.

---

## 4. Register the Endpoint with the Raizen Server

This step is performed once per machine by a **Raizen administrator** from their own workstation.

The endpoint service will start polling immediately but won't be trusted until this registration is complete. Unregistered endpoints are silently rejected.

### 4.1 Get an Admin Bearer Token

On the admin's machine, acquire a token for the Raizen API using the Azure CLI or PowerShell:

```powershell
# Using Azure CLI
az login --tenant YOUR_TENANT_ID
$token = az account get-access-token `
    --resource api://YOUR_API_CLIENT_ID `
    --query accessToken -o tsv
```

Or using MSAL PowerShell (install once: `Install-Module MSAL.PS`):

```powershell
$result = Get-MsalToken `
    -ClientId "YOUR_API_CLIENT_ID" `
    -TenantId "YOUR_TENANT_ID" `
    -Scopes   "api://YOUR_API_CLIENT_ID/.default" `
    -Interactive
$token = $result.AccessToken
```

### 4.2 Register the Endpoint

```powershell
# Values from the installer output
$machineId = "a1b2c3d4-e5f6-7890-ab12-cd34ef567890"
$apiKey    = "Kx9mP2-vQ8rT5nZ1jLhG..."
$machineName = "DESKTOP-ABC123"

$serverUrl = "https://raizen.contoso.com"

$headers = @{
    "Authorization"     = "Bearer $token"
    "X-Raizen-MachineId" = $machineId
    "X-Raizen-NewApiKey" = $apiKey
    "Content-Type"      = "application/json"
}

$body = @{ MachineName = $machineName } | ConvertTo-Json

$response = Invoke-RestMethod `
    -Uri "$serverUrl/api/v1/endpoints/register" `
    -Method POST `
    -Headers $headers `
    -Body $body

Write-Host "Registered endpoint ID: $($response.id)"
```

A successful response looks like:
```json
{
  "id": "f1e2d3c4-...",
  "machineId": "a1b2c3d4-...",
  "machineName": "DESKTOP-ABC123",
  "isEnabled": true,
  "registeredAt": "2025-01-15T10:00:00Z"
}
```

The endpoint is now trusted. The service will receive approved elevation requests on its next poll cycle (default: 30 seconds).

### 4.3 Verify Registration in the Admin Portal

1. Open `https://raizen.contoso.com` in your browser
2. Navigate to **Endpoints** in the left menu
3. You should see `DESKTOP-ABC123` with status **Enabled** and a recent `LastSeenAt` timestamp

---

## 5. Verify the Service is Running

On the **endpoint machine**:

```powershell
# Check service status
Get-Service -Name RaizenEndpoint

# Expected output:
# Status   Name               DisplayName
# -------  ----               -----------
# Running  RaizenEndpoint     Raizen Elevation Service
```

Check the log for any errors:

```powershell
Get-Content "C:\ProgramData\Raizen\Logs\service-*.log" -Tail 30
```

A healthy log looks like:
```
[INF] Raizen elevation worker started.
[INF] Heartbeat sent successfully.
[INF] Poll cycle: 0 pending requests.
```

If you see repeated warnings about the server URL, double-check:
- The `ServerUrl` in `C:\ProgramData\Raizen\raizen-config.json`
- Outbound firewall rules on the endpoint
- That the service was registered in step 4

---

## 6. Deploy the Tray Application to Users

The tray app is registered in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` by the installer, so it launches automatically when **any user** signs in.

Users will see the **Raizen shield icon** in their system tray.

### 6.1 First-time user experience

1. User clicks the tray icon or double-clicks it
2. Selects **"Request Elevated Action..."**
3. Picks an action from the dropdown (populated from the server)
4. Fills in parameters, justification, and optional ticket reference
5. Clicks **"Submit Request"**
6. A confirmation dialog shows the request ID
7. The user can track status via **"My Requests..."** in the tray menu
8. When approved and executed, a balloon notification appears

### 6.2 If the tray doesn't appear automatically

Users can launch it manually:
```
C:\Program Files\Raizen\Tray\Raizen.Endpoint.Tray.exe
```

Or add a shortcut to `%ALLUSERSPROFILE%\Microsoft\Windows\Start Menu\Programs\Startup\`.

---

## 7. Changing the Server URL Later

If the Raizen server moves to a new hostname or IP, update the config file on each endpoint. **No service restart is required** — the service watches the file for changes.

### Option A — Edit the file directly (single machine)

Open `C:\ProgramData\Raizen\raizen-config.json` in Notepad (as Administrator):

```json
{
  "ServerUrl": "https://NEW-raizen.contoso.com",
  ...
}
```

Save the file. The service picks up the new URL within a few seconds.

### Option B — PowerShell (single machine, no editor)

```powershell
$configPath = "C:\ProgramData\Raizen\raizen-config.json"
$config = Get-Content $configPath | ConvertFrom-Json
$config.ServerUrl = "https://NEW-raizen.contoso.com"
$config | ConvertTo-Json -Depth 5 | Set-Content $configPath -Encoding UTF8
```

### Option C — Intune / Group Policy (all machines at once)

Deploy a PowerShell script via Intune that runs the Option B command across all managed endpoints. See [section 9](#9-deploying-at-scale-intune--group-policy) for details.

### Option D — Custom config path

If you want the config file in a different location (e.g., on a network share for centralised management):

1. Update the service and tray autostart entries to pass the new path:
```powershell
# Update service binary path
sc.exe config RaizenEndpoint binpath= `
    "`"C:\Program Files\Raizen\Service\Raizen.Endpoint.Service.exe`" --config=`"\\fileserver\Raizen\raizen-config.json`""

# Update tray autostart
Set-ItemProperty `
    -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name "RaizenTray" `
    -Value "`"C:\Program Files\Raizen\Tray\Raizen.Endpoint.Tray.exe`" --config=`"\\fileserver\Raizen\raizen-config.json`""
```

2. Or set the environment variable:
```powershell
[System.Environment]::SetEnvironmentVariable(
    "RAIZEN_CONFIG_PATH",
    "\\fileserver\Raizen\raizen-config.json",
    "Machine")
```

---

## 8. Uninstalling

Run the uninstaller script from an elevated PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

# Remove service and tray (keep config/logs for audit purposes)
.\Uninstall-RaizenEndpoint.ps1

# Remove everything including config, logs, and ApprovedScripts
.\Uninstall-RaizenEndpoint.ps1 -RemoveConfig
```

Alternatively, step by step:

```powershell
# 1. Stop and remove the service
Stop-Service RaizenEndpoint -Force
sc.exe delete RaizenEndpoint

# 2. Remove tray autostart
Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name "RaizenTray" -ErrorAction SilentlyContinue

# 3. Remove binaries
Remove-Item "C:\Program Files\Raizen" -Recurse -Force

# 4. (Optional) Remove config and logs
Remove-Item "C:\ProgramData\Raizen" -Recurse -Force
```

---

## 9. Deploying at Scale (Intune / Group Policy)

### 9.1 Package for Intune (Win32 App)

1. Create a folder: `RaizenEndpoint-IntunePackage\`
2. Copy into it:
   - `publish\service\` folder
   - `publish\tray\` folder
   - `Install-RaizenEndpoint.ps1`
   - `Uninstall-RaizenEndpoint.ps1`
3. Package with the **Microsoft Win32 Content Prep Tool**:
   ```powershell
   .\IntuneWinAppUtil.exe `
       -c RaizenEndpoint-IntunePackage `
       -s Install-RaizenEndpoint.ps1 `
       -o . `
       -q
   ```
4. Upload the `.intunewin` file to **Intune → Apps → Windows → Add → Windows app (Win32)**

**Install command:**
```
powershell.exe -ExecutionPolicy Bypass -File Install-RaizenEndpoint.ps1 -ServerUrl "https://raizen.contoso.com"
```

**Uninstall command:**
```
powershell.exe -ExecutionPolicy Bypass -File Uninstall-RaizenEndpoint.ps1 -RemoveConfig
```

**Detection rule:**
- Type: Registry
- Key path: `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\RaizenEndpoint`
- Value name: `ImagePath`
- Detection method: Key exists

### 9.2 Automate Registration via Intune

Create a second Intune **PowerShell script** that runs once after the app installs:

```powershell
# RegisterRaizenEndpoint.ps1
# Deploy as Intune PowerShell script: Run as SYSTEM, Run in 64-bit

$serverUrl  = "https://raizen.contoso.com"
$adminToken = "EMBED_OR_RETRIEVE_FROM_KEY_VAULT"   # see note below

$configPath = "C:\ProgramData\Raizen\raizen-config.json"
$config     = Get-Content $configPath | ConvertFrom-Json

$headers = @{
    "Authorization"      = "Bearer $adminToken"
    "X-Raizen-MachineId" = $config.MachineId
    "X-Raizen-NewApiKey" = $config.ApiKey
    "Content-Type"       = "application/json"
}

$body = @{
    MachineName = $env:COMPUTERNAME
    OsVersion   = [System.Environment]::OSVersion.VersionString
} | ConvertTo-Json

try {
    Invoke-RestMethod `
        -Uri "$serverUrl/api/v1/endpoints/register" `
        -Method POST `
        -Headers $headers `
        -Body $body | Out-Null
    Write-Host "Endpoint registered successfully."
} catch {
    Write-Error "Registration failed: $_"
    exit 1
}
```

> **Token security note:** For Intune scripts, retrieve the admin token using a **Managed Identity** or store it in **Azure Key Vault** and retrieve it at runtime with `az keyvault secret show`. Do not embed tokens in plain text.

### 9.3 Group Policy Preferences (GPP) — Alternative

If you use GPO instead of Intune:

1. **Computer Configuration → Preferences → Windows Settings → Files**
   - Copy `raizen-config.json` from a share to `C:\ProgramData\Raizen\`

2. **Computer Configuration → Preferences → Windows Settings → Registry**
   - Set `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\RaizenTray`

3. **Computer Configuration → Preferences → Control Panel Settings → Services**
   - Start service `RaizenEndpoint`

---

## 10. Troubleshooting

### Service fails to start

```powershell
# Check Windows Event Log for the specific error
Get-EventLog -LogName System -Source "Service Control Manager" -Newest 20 |
    Where-Object { $_.Message -like "*Raizen*" }

# Check the Raizen log file
Get-Content "C:\ProgramData\Raizen\Logs\service-*.log" -Tail 50
```

**Common causes:**

| Symptom | Cause | Fix |
|---|---|---|
| `1053: service did not respond` | .NET runtime not installed | Install .NET 8 Desktop Runtime |
| `The system cannot find the file` | Binary path wrong | Verify `C:\Program Files\Raizen\Service\Raizen.Endpoint.Service.exe` exists |
| Config loaded but ServerUrl empty | Config file malformed | Re-run installer or check JSON syntax |
| `Access denied` writing logs | Log directory permissions | `icacls "C:\ProgramData\Raizen\Logs" /grant "SYSTEM:(OI)(CI)F"` |

---

### Service runs but requests are never picked up

```powershell
# Verify the endpoint is registered and enabled on the server
# (check the admin portal: Endpoints tab)

# Check if the endpoint can reach the server
Test-NetConnection -ComputerName raizen.contoso.com -Port 443

# Check the service log for HTTP errors
Select-String -Path "C:\ProgramData\Raizen\Logs\service-*.log" `
    -Pattern "(WRN|ERR|Could not reach)"
```

**Common causes:**

| Symptom | Cause | Fix |
|---|---|---|
| `Could not reach Raizen server` | Outbound firewall blocks port | Open outbound TCP 443 to server IP |
| `401 Unauthorized` on every poll | Endpoint not registered | Complete step 4 |
| `401` after re-imaging | API key changed | Re-run installer; re-register |
| Requests show Approved but never execute | Service polling but action handler missing | Check ActionType is in the supported list |

---

### Tray app doesn't show the action list

The tray calls `GET /api/v1/actions` which requires a valid API key. This means the service must be running and the endpoint must be registered.

```powershell
# Quick test: does the endpoint auth work?
$config = Get-Content "C:\ProgramData\Raizen\raizen-config.json" | ConvertFrom-Json

Invoke-WebRequest `
    -Uri "$($config.ServerUrl)/api/v1/actions" `
    -Headers @{
        "X-Raizen-MachineId" = $config.MachineId
        "X-Raizen-ApiKey"    = $config.ApiKey
    } `
    -UseBasicParsing
```
Expected: `200 OK` with a JSON array. If you get `401`, the endpoint is not registered or the API key is wrong.

---

### Changing the server URL — service isn't picking up the change

The `FileSystemWatcher` requires the service to have read access to the config directory. Verify:

```powershell
# Check access
icacls "C:\ProgramData\Raizen\raizen-config.json"

# Force a re-read by touching the file
(Get-Item "C:\ProgramData\Raizen\raizen-config.json").LastWriteTime = Get-Date

# Or restart the service
Restart-Service RaizenEndpoint
```

---

## Quick Reference

```powershell
# ── Config file location ──────────────────────────────────────────────────────
C:\ProgramData\Raizen\raizen-config.json

# ── Log file location ─────────────────────────────────────────────────────────
C:\ProgramData\Raizen\Logs\service-YYYYMMDD.log

# ── Service management ────────────────────────────────────────────────────────
Start-Service   RaizenEndpoint
Stop-Service    RaizenEndpoint
Restart-Service RaizenEndpoint
Get-Service     RaizenEndpoint

# ── Live log tail ─────────────────────────────────────────────────────────────
Get-Content "C:\ProgramData\Raizen\Logs\service-$(Get-Date -Format yyyyMMdd).log" -Tail 40 -Wait

# ── Approved scripts directory ────────────────────────────────────────────────
C:\ProgramData\Raizen\ApprovedScripts\

# ── Binary locations ──────────────────────────────────────────────────────────
C:\Program Files\Raizen\Service\Raizen.Endpoint.Service.exe
C:\Program Files\Raizen\Tray\Raizen.Endpoint.Tray.exe
```

---

*Raizen Endpoint Installation Guide — v1.0*
