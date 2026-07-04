# Raizen Server — IT Setup Guide

**Audience:** IT administrators deploying the Raizen server on Windows Server
**Time required:** 45–90 minutes for first deployment
**Requires:** Windows Server 2019 or 2022; local administrator rights; a TLS certificate; DNS records created

---

## Architecture Overview

```
  Endpoints / Admin browsers
          │  HTTPS :443
  ┌───────▼────────────────────────┐
  │           IIS (ARR)            │
  │  api.raizen.contoso.com        │──► http://localhost:5001  (Raizen API service)
  │  admin.raizen.contoso.com      │──► http://localhost:5002  (Raizen Web service)
  └────────────────────────────────┘
                                          │
                                   PostgreSQL :5432
                                   (localhost only)
```

All three components (API, Web, PostgreSQL) run as Windows Services on the same server. IIS terminates HTTPS and forwards traffic to each service. Nothing is exposed to the network except port 443 through IIS.

---

## Before You Start

Ensure the following are in place:

- [ ] DNS A records created: `api.raizen.contoso.com` and `admin.raizen.contoso.com` both pointing to this server's IP
- [ ] TLS certificate obtained as a `.pfx` file covering both hostnames (from your internal CA or Let's Encrypt)
- [ ] You have the PFX password (or the file is password-free)
- [ ] `raizen-server-<version>.zip` is copied to the server (e.g. `C:\Temp\`)
- [ ] You are logged in as a local Administrator or Domain Admin

---

## Step 1 — Install Prerequisites

Open **PowerShell as Administrator** and run the following.

### 1.1 — .NET 8 ASP.NET Core Runtime

```powershell
# Download and install .NET 8 ASP.NET Core Runtime (x64)
$dotnetUrl = "https://download.microsoft.com/download/dotnet/8.0/aspnetcore-runtime-8.0.x-win-x64.exe"
Invoke-WebRequest $dotnetUrl -OutFile "$env:TEMP\dotnet8-runtime.exe"
Start-Process "$env:TEMP\dotnet8-runtime.exe" -ArgumentList "/install /quiet /norestart" -Wait
# Verify
dotnet --list-runtimes | Select-String "ASP"
```

Expected output: `Microsoft.AspNetCore.App 8.0.x`

### 1.2 — PostgreSQL 16

```powershell
# Download PostgreSQL 16 interactive installer
$pgUrl = "https://get.enterprisedb.com/postgresql/postgresql-16.x-windows-x64.exe"
Invoke-WebRequest $pgUrl -OutFile "$env:TEMP\pg16-setup.exe"

# Install silently (no pgAdmin, no Stack Builder)
Start-Process "$env:TEMP\pg16-setup.exe" -Wait -ArgumentList `
    "--mode unattended",
    "--superpassword `"<STRONG-POSTGRES-SUPERUSER-PASSWORD>`"",
    "--serverport 5432",
    "--enable-components server",
    "--disable-components pgAdmin,stackbuilder"
```

> Replace `<STRONG-POSTGRES-SUPERUSER-PASSWORD>` with a strong password. Store it in your password vault — you will need it for DB administration.

### 1.3 — IIS with ARR and URL Rewrite

```powershell
# Install IIS role and management tools
Install-WindowsFeature -Name Web-Server, Web-Mgmt-Tools, Web-Mgmt-Console -IncludeManagementTools

# Download and install ARR 3.0
$arrUrl  = "https://download.microsoft.com/download/E/9/8/E9849D6A-020E-47E4-9FD0-A023E99B54EB/requestRouter_amd64.msi"
$wrUrl   = "https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi"
Invoke-WebRequest $arrUrl -OutFile "$env:TEMP\arr.msi"
Invoke-WebRequest $wrUrl  -OutFile "$env:TEMP\urlrewrite.msi"
Start-Process msiexec -ArgumentList "/i `"$env:TEMP\arr.msi`" /quiet"    -Wait
Start-Process msiexec -ArgumentList "/i `"$env:TEMP\urlrewrite.msi`" /quiet" -Wait

# Enable ARR proxy mode
$arrConfig = [System.IO.Path]::Combine($env:SystemRoot, 'system32\inetsrv\config\applicationHost.config')
$cmd = "`"$env:SystemRoot\system32\inetsrv\appcmd.exe`" set config -section:system.webServer/proxy /enabled:true /commit:apphost"
Invoke-Expression $cmd
```

---

## Step 2 — Extract the Deployment Package

```powershell
$dest = "C:\Temp\RaizenServer"
Expand-Archive "C:\Temp\raizen-server-<version>.zip" -DestinationPath $dest -Force
cd $dest
```

---

## Step 3 — Run the Setup Script

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
.\scripts\Install-RaizenServer.ps1
```

The script will prompt you for:

| Prompt | Example | Notes |
|---|---|---|
| API external URL | `https://api.raizen.contoso.com` | Must match DNS |
| Web external URL | `https://admin.raizen.contoso.com` | Must match DNS |
| PostgreSQL `raizen` user password | *(choose strong password)* | Used only on localhost; store in vault |
| Web encryption key | *(auto-generated or enter your own)* | **Write this down — losing it locks out all admin accounts** |
| Path to TLS certificate (.pfx) | `C:\Certs\raizen.pfx` | SAN cert covering both hostnames |
| PFX password | *(enter or leave blank)* | |

The script then:
1. Creates the `raizen` PostgreSQL database and user
2. Creates `%ProgramFiles%\Raizen\Api\` and `Web\` directories with correct ACLs
3. Copies API and Web binaries
4. Writes `appsettings.Production.json` for each service
5. Installs `RaizenApi` and `RaizenWeb` as Windows Services
6. Imports the TLS certificate into IIS
7. Creates IIS sites with ARR reverse proxy rules
8. Starts both services
9. Schedules a daily database backup task

---

## Step 4 — Verify the Installation

### 4.1 Check Windows Services

```powershell
Get-Service RaizenApi, RaizenWeb | Select-Object Name, Status, StartType
```

Expected:
```
Name       Status  StartType
----       ------  ---------
RaizenApi  Running Automatic
RaizenWeb  Running Automatic
```

### 4.2 Test the API

```powershell
# Should return 401 Unauthorized (API is running and responding)
Invoke-WebRequest https://api.raizen.contoso.com/api/v1/requests -UseBasicParsing
```

### 4.3 Test the Web Portal

Open a browser and navigate to `https://admin.raizen.contoso.com`. You should see the Raizen login page with a valid TLS padlock.

### 4.4 Check IIS

Open **IIS Manager** → expand the server → **Sites**. You should see:
- `RaizenApi` — bound to `api.raizen.contoso.com:443`
- `RaizenWeb` — bound to `admin.raizen.contoso.com:443`

---

## Step 5 — First Login and Security Setup

### 5.1 Change the default admin password

1. Open `https://admin.raizen.contoso.com`
2. Log in: **Username** `Admin` — **Password** `Admin`
3. You will be redirected to **Change Password** — do this immediately
4. Set a strong password (12+ characters, mixed) and store it in your password vault

### 5.2 Configure email notifications (optional)

1. Navigate to **Settings → Notifications**
2. Enter SMTP details (host, port, TLS, credentials, from address)
3. Add recipient email addresses for alerts
4. Enable triggers: **On Submit**, **On Approved**, **On Denied**
5. Click **Save**, then **Send Test Email** to verify

### 5.3 Configure action definitions

Navigate to **Action Definitions**. For each action type you want to make available:
- Toggle **Enabled**
- Set **Approvers** — the admin accounts that can approve this action type
- Set **Approval Window** — how long an approved request stays valid (default 30 min)
- Optionally enable **Auto-Approve** for low-risk, trusted action types

---

## Step 6 — Deploy Endpoints

With the server running, deploy the Raizen Endpoint MSI to managed workstations. See [endpoint-setup-guide.md](endpoint-setup-guide.md).

Once deployed and registered, endpoints appear in **Admin Portal → Endpoints** within 5 minutes of their first heartbeat.

---

## Configuration Files

### API — `%ProgramFiles%\Raizen\Api\appsettings.Production.json`

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=raizen;Username=raizen;Password=<password>"
  },
  "AllowedHosts": "api.raizen.contoso.com"
}
```

### Web — `%ProgramFiles%\Raizen\Web\appsettings.Production.json`

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=raizen;Username=raizen;Password=<password>"
  },
  "Security": {
    "EncryptionKey": "<your-encryption-key>"
  },
  "RaizenApi": {
    "BaseUrl": "http://localhost:5001"
  },
  "AllowedHosts": "admin.raizen.contoso.com"
}
```

> **After editing either file:** restart the relevant service:
> ```powershell
> Restart-Service RaizenApi   # or RaizenWeb
> ```

---

## Updating Raizen

```powershell
# 1. Stop services
Stop-Service RaizenApi, RaizenWeb

# 2. Replace binaries (preserve appsettings.Production.json)
Copy-Item "C:\Temp\RaizenServer-new\api\*" "%ProgramFiles%\Raizen\Api\" -Recurse -Force -Exclude appsettings.Production.json
Copy-Item "C:\Temp\RaizenServer-new\web\*" "%ProgramFiles%\Raizen\Web\" -Recurse -Force -Exclude appsettings.Production.json

# 3. Start services (EF Core migrations run automatically on startup)
Start-Service RaizenApi, RaizenWeb

# 4. Verify
Get-Service RaizenApi, RaizenWeb | Select-Object Name, Status
```

---

## Backup and Restore

### Manual backup

```powershell
.\scripts\Backup-RaizenDb.ps1
# Output: C:\ProgramData\Raizen\Server\Backups\raizen-<date>.dump
```

### Automated daily backup

The setup script creates a **Task Scheduler** task named `RaizenDatabaseBackup` that runs at 02:00 daily. To verify:

```powershell
Get-ScheduledTask -TaskName RaizenDatabaseBackup | Select-Object TaskName, State
```

Backups older than 30 days are automatically deleted.

### Restore from backup

```powershell
# Stop services first
Stop-Service RaizenApi, RaizenWeb

.\scripts\Restore-RaizenDb.ps1 -BackupFile "C:\ProgramData\Raizen\Server\Backups\raizen-2026-01-15.dump"

Start-Service RaizenApi, RaizenWeb
```

---

## Viewing Logs

```powershell
# API logs (rolling daily)
Get-ChildItem "C:\ProgramData\Raizen\Server\Logs\Api\"
Get-Content  "C:\ProgramData\Raizen\Server\Logs\Api\raizen-api-20260115.log" -Tail 100

# Web logs
Get-Content  "C:\ProgramData\Raizen\Server\Logs\Web\raizen-web-20260115.log" -Tail 100

# Windows Event Log (service start/stop errors)
Get-EventLog -LogName System -Source "Service Control Manager" -Newest 20 |
    Where-Object { $_.Message -like "*Raizen*" }
```

---

## Firewall Rules

The setup script creates these Windows Firewall rules automatically. To verify or add manually:

```powershell
# Allow HTTPS inbound (endpoints + admin browsers)
New-NetFirewallRule -DisplayName "Raizen HTTPS" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow

# Block direct access to API and Web ports from outside
New-NetFirewallRule -DisplayName "Block Raizen API direct" -Direction Inbound -Protocol TCP -LocalPort 5001 -Action Block
New-NetFirewallRule -DisplayName "Block Raizen Web direct" -Direction Inbound -Protocol TCP -LocalPort 5002 -Action Block

# PostgreSQL — localhost only (no inbound rule needed; it binds to 127.0.0.1)
```

---

## Troubleshooting

### Service fails to start

```powershell
# Check Windows Event Log
Get-EventLog -LogName Application -Source ".NET Runtime" -Newest 10
# Check service logs
Get-Content "C:\ProgramData\Raizen\Server\Logs\Api\*.log" -Tail 50
```

Common causes:
- `appsettings.Production.json` missing or has invalid JSON → re-check the file
- Wrong PostgreSQL password → correct `ConnectionStrings.Default` and restart
- Port 5001 or 5002 already in use → `netstat -ano | findstr :5001`
- .NET 8 Runtime not installed → re-run Step 1.1

### IIS shows 502 Bad Gateway

The IIS site is running but cannot reach the upstream service:
1. Check `RaizenApi` or `RaizenWeb` service is running: `Get-Service RaizenApi`
2. Test the upstream directly: `Invoke-WebRequest http://localhost:5001 -UseBasicParsing`
3. Check IIS ARR proxy is enabled: IIS Manager → Server → Application Request Routing Cache → Server Proxy Settings → Enable proxy ✓

### TLS certificate errors in browser

```powershell
# Check the cert is in the right store
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*raizen*" }
# Re-bind in IIS Manager: Site → Bindings → Edit → select the correct certificate
```

### Admin password forgotten

Connect to PostgreSQL and delete the admin account (it will be re-seeded with `Admin`/`Admin` on next API start):

```powershell
$pgBin = "C:\Program Files\PostgreSQL\16\bin"
& "$pgBin\psql.exe" -U postgres -d raizen -c "DELETE FROM admin_users WHERE ""Username"" = 'Admin';"
Restart-Service RaizenApi
# Log in with Admin / Admin and immediately change the password
```

### Endpoint shows "Offline" in portal

- Verify the endpoint can reach `https://api.raizen.contoso.com` (port 443)
- Check Windows Firewall on the **server** allows inbound 443
- Check the endpoint's `raizen-config.json` has the correct `ServerUrl`
- Verify the endpoint is registered and enabled in **Admin Portal → Endpoints**

---

## Uninstall

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
.\scripts\Uninstall-RaizenServer.ps1
```

The uninstaller will:
1. Stop and remove `RaizenApi` and `RaizenWeb` services
2. Remove IIS sites and bindings
3. Remove TLS certificate from IIS (does not delete from certificate store)
4. Remove binaries from `%ProgramFiles%\Raizen\`
5. Remove the scheduled backup task
6. **Prompt** whether to drop the `raizen` database (default: No)
7. **Prompt** whether to uninstall PostgreSQL (default: No)
