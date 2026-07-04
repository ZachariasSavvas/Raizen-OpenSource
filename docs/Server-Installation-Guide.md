# Raizen Server — Installation Guide (Windows)

> **Target OS:** Windows Server 2019 / 2022 (or Windows 10/11 for dev/pilot)
> **Estimated time:** 60–90 minutes

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Entra ID App Registrations](#2-entra-id-app-registrations)
3. [Install PostgreSQL](#3-install-postgresql)
4. [Build & Publish the Server Projects](#4-build--publish-the-server-projects)
5. [Configure the API (`appsettings.json`)](#5-configure-the-api)
6. [Configure the Admin Web (`appsettings.json`)](#6-configure-the-admin-web)
7. [Install as Windows Services](#7-install-as-windows-services)
8. [Configure Windows Firewall](#8-configure-windows-firewall)
9. [Set Up IIS as a Reverse Proxy (Recommended)](#9-set-up-iis-as-a-reverse-proxy-recommended)
10. [Run the Database Migration](#10-run-the-database-migration)
11. [Create the First Admin Account & Roles](#11-create-the-first-admin-account--roles)
12. [Verify the Installation](#12-verify-the-installation)
13. [Hardening Checklist](#13-hardening-checklist)

---

## 1. Prerequisites

Install the following on the server machine before continuing.

### 1.1 .NET 8 Runtime

Download and install the **ASP.NET Core Runtime 8.x** (not the SDK, unless you plan to build on the server):

```
https://dotnet.microsoft.com/en-us/download/dotnet/8.0
→ "ASP.NET Core Runtime" → Windows x64 installer
```

Verify:
```powershell
dotnet --version
# Expected: 8.x.x
```

### 1.2 Git (optional, for cloning)

```
https://git-scm.com/download/win
```

### 1.3 Windows Firewall management (already built-in)

No additional tools required.

---

## 2. Entra ID App Registrations

You need **two** app registrations in your Microsoft Entra ID (Azure AD) tenant: one for the API, one for the admin web site.

### 2.1 API App Registration (`Raizen-API`)

1. Open **Azure Portal → Entra ID → App registrations → New registration**
2. Name: `Raizen-API`
3. Supported account types: **Accounts in this organizational directory only**
4. Redirect URI: leave blank for now → **Register**

After creation:

**Expose an API:**
1. Left menu → **Expose an API**
2. Click **Set** next to Application ID URI → accept the default `api://<client-id>` → **Save**
3. Click **Add a scope**:
   - Scope name: `Elevation.ReadWrite`
   - Who can consent: **Admins only**
   - Admin consent display name: `Raizen Elevation Read/Write`
   - → **Add scope**

**App Roles:**
1. Left menu → **App roles → Create app role**
2. Create three roles:

| Display Name     | Value            | Allowed member types |
|------------------|------------------|----------------------|
| Raizen Admin     | `Raizen.Admin`   | Users/Groups         |
| Raizen Approver  | `Raizen.Approver`| Users/Groups         |
| Raizen Operator  | `Raizen.Operator`| Users/Groups         |

3. Note the **Application (client) ID** — this is `AZURE_CLIENT_ID_API`
4. Note the **Directory (tenant) ID** — this is `AZURE_TENANT_ID`

---

### 2.2 Web App Registration (`Raizen-Web`)

1. **New registration**
2. Name: `Raizen-Web`
3. Supported account types: **Accounts in this organizational directory only**
4. Redirect URI: **Web** → `https://your-server-hostname:5002/signin-oidc`
   *(Replace `your-server-hostname` with your actual FQDN or IP)*
5. **Register**

After creation:

**Authentication:**
1. Left menu → **Authentication**
2. Add **Logout URL**: `https://your-server-hostname:5002/signout-callback-oidc`
3. Under **Implicit grant**: leave unchecked
4. **Save**

**Client Secret:**
1. Left menu → **Certificates & secrets → New client secret**
2. Description: `Raizen-Web-Secret`, Expires: 24 months
3. **Add** → copy the **Value** immediately (you won't see it again) — this is `AZURE_CLIENT_SECRET_WEB`

**API Permissions:**
1. Left menu → **API permissions → Add a permission → My APIs → Raizen-API**
2. Select `Elevation.ReadWrite` → **Add permissions**
3. Click **Grant admin consent for [your tenant]**

**App Roles Assignment (assign users/groups):**
1. Go to **Entra ID → Enterprise applications → Raizen-API**
2. Left menu → **Users and groups → Add user/group**
3. Assign your admin users to `Raizen.Admin`; approvers to `Raizen.Approver`; read-only ops to `Raizen.Operator`

Note the **Application (client) ID** for the Web registration — this is `AZURE_CLIENT_ID_WEB`.

---

## 3. Install PostgreSQL

### 3.1 Download & Install

Download PostgreSQL 16 for Windows:
```
https://www.postgresql.org/download/windows/
→ Download the installer → postgresql-16.x-windows-x64.exe
```

During installation:
- **Password**: set a strong password for the `postgres` superuser (save it!)
- **Port**: `5432` (default)
- **Locale**: your locale or Default
- **Stack Builder**: you can skip it

### 3.2 Create the Raizen Database and User

Open **pgAdmin** (installed with PostgreSQL) or **psql** as the `postgres` user:

```powershell
# Open psql (adjust path to match your PostgreSQL version)
& "C:\Program Files\PostgreSQL\16\bin\psql.exe" -U postgres
```

Run these SQL commands:
```sql
-- Create a dedicated user
CREATE USER raizen WITH PASSWORD 'CHANGE_ME_STRONG_PASSWORD';

-- Create the database owned by that user
CREATE DATABASE raizen OWNER raizen;

-- Connect to the raizen database
\c raizen

-- Grant privileges
GRANT ALL PRIVILEGES ON DATABASE raizen TO raizen;
GRANT ALL PRIVILEGES ON SCHEMA public TO raizen;

-- Exit
\q
```

> **Important:** Replace `CHANGE_ME_STRONG_PASSWORD` with a strong, random password.
> Store it in a password manager — you will need it in step 5.

### 3.3 Configure PostgreSQL to Listen on Localhost Only

Edit `C:\Program Files\PostgreSQL\16\data\postgresql.conf`:
```
listen_addresses = 'localhost'   # was '*' by default
```

Edit `C:\Program Files\PostgreSQL\16\data\pg_hba.conf` — ensure this line exists:
```
host    raizen    raizen    127.0.0.1/32    scram-sha-256
```

Restart PostgreSQL:
```powershell
Restart-Service -Name postgresql-x64-16
```

---

## 4. Build & Publish the Server Projects

Do this on a **build machine** (or the server itself if you have the SDK installed).

### 4.1 Install .NET 8 SDK (build machine only)

```
https://dotnet.microsoft.com/en-us/download/dotnet/8.0
→ SDK → Windows x64
```

### 4.2 Clone / Copy the Source

```powershell
git clone <your-repo-url> C:\Build\Raizen
cd C:\Build\Raizen
```

### 4.3 Publish Both Server Projects

```powershell
# API
dotnet publish src/Raizen.Server/Raizen.Server.Api `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o C:\Build\publish\api

# Admin Web
dotnet publish src/Raizen.Server/Raizen.Server.Web `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o C:\Build\publish\web
```

### 4.4 Copy Publish Output to the Server

Copy the two publish directories to the server machine:
```
C:\Raizen\API\    ← contents of publish\api
C:\Raizen\Web\    ← contents of publish\web
```

You can use `robocopy`, a file share, or any transfer method.

---

## 5. Configure the API

On the **server machine**, open:
```
C:\Raizen\API\appsettings.json
```

Fill in all the values:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    }
  },

  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=raizen;Username=raizen;Password=CHANGE_ME_STRONG_PASSWORD"
  },

  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "YOUR_TENANT_ID",
    "ClientId": "YOUR_API_CLIENT_ID",
    "Audience": "api://YOUR_API_CLIENT_ID"
  },

  "IpRateLimiting": {
    "EnableEndpointRateLimiting": false,
    "StackBlockedRequests": false,
    "RealIpHeader": "X-Real-IP",
    "GeneralRules": [
      { "Endpoint": "*", "Period": "1m", "Limit": 300 }
    ]
  },

  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5001"
      }
    }
  },

  "AllowedHosts": "*"
}
```

> **Note:** The API binds to `localhost:5001` only. IIS acts as the public reverse proxy — so the API never needs to be on a public port directly.

Create the log directory:
```powershell
New-Item -ItemType Directory -Path C:\Raizen\API\logs -Force
```

---

## 6. Configure the Admin Web

Open:
```
C:\Raizen\Web\appsettings.json
```

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    }
  },

  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=raizen;Username=raizen;Password=CHANGE_ME_STRONG_PASSWORD"
  },

  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "YOUR_TENANT_ID",
    "ClientId": "YOUR_WEB_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "CallbackPath": "/signin-oidc",
    "SignedOutCallbackPath": "/signout-callback-oidc"
  },

  "RaizenApi": {
    "BaseUrl": "http://localhost:5001"
  },

  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5002"
      }
    }
  },

  "AllowedHosts": "*"
}
```

Create the log directory:
```powershell
New-Item -ItemType Directory -Path C:\Raizen\Web\logs -Force
```

---

## 7. Install as Windows Services

Both applications run as **Windows Services** under a dedicated low-privilege account.

### 7.1 Create a Dedicated Service Account

```powershell
# Create local user (no interactive logon, no admin rights)
$pass = ConvertTo-SecureString "CHANGE_ME_SVC_PASSWORD" -AsPlainText -Force
New-LocalUser -Name "raizen-svc" `
    -Password $pass `
    -FullName "Raizen Service Account" `
    -Description "Runs Raizen API and Web services" `
    -PasswordNeverExpires `
    -UserMayNotChangePassword

# Grant "Log on as a service" right
$sidStr = (New-Object System.Security.Principal.NTAccount("raizen-svc")).Translate(
    [System.Security.Principal.SecurityIdentifier]).Value

secedit /export /cfg C:\Temp\secpol.cfg /quiet
(Get-Content C:\Temp\secpol.cfg) -replace
    '(SeServiceLogonRight\s*=\s*)(.*)',
    "`$1`$2,*$sidStr" |
    Set-Content C:\Temp\secpol.cfg
secedit /import /cfg C:\Temp\secpol.cfg /quiet
Remove-Item C:\Temp\secpol.cfg
```

### 7.2 Grant the Service Account Read Access to App Directories

```powershell
$dirs = @("C:\Raizen\API", "C:\Raizen\Web")
foreach ($dir in $dirs) {
    $acl = Get-Acl $dir
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "raizen-svc", "ReadAndExecute",
        "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($rule)
    # Allow write to the logs subdirectory
    Set-Acl $dir $acl
}

# Allow the service account to write logs
foreach ($dir in @("C:\Raizen\API\logs", "C:\Raizen\Web\logs")) {
    $acl = Get-Acl $dir
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "raizen-svc", "Modify",
        "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($rule)
    Set-Acl $dir $acl
}
```

### 7.3 Install the API Service

```powershell
$apiExe = "C:\Raizen\API\Raizen.Server.Api.exe"
$apiSvc = "RaizenAPI"
$svcPass = "CHANGE_ME_SVC_PASSWORD"

New-Service `
    -Name $apiSvc `
    -BinaryPathName $apiExe `
    -DisplayName "Raizen API" `
    -Description "Raizen JIT elevation REST API" `
    -StartupType Automatic `
    -Credential (New-Object PSCredential(".\raizen-svc",
        (ConvertTo-SecureString $svcPass -AsPlainText -Force)))

# Set failure recovery
sc.exe failure $apiSvc reset= 86400 actions= restart/5000/restart/10000/restart/30000

Start-Service -Name $apiSvc
Get-Service -Name $apiSvc   # Status should be: Running
```

### 7.4 Install the Admin Web Service

```powershell
$webExe = "C:\Raizen\Web\Raizen.Server.Web.exe"
$webSvc = "RaizenWeb"

New-Service `
    -Name $webSvc `
    -BinaryPathName $webExe `
    -DisplayName "Raizen Admin Web" `
    -Description "Raizen JIT elevation admin portal" `
    -StartupType Automatic `
    -Credential (New-Object PSCredential(".\raizen-svc",
        (ConvertTo-SecureString $svcPass -AsPlainText -Force)))

sc.exe failure $webSvc reset= 86400 actions= restart/5000/restart/10000/restart/30000

Start-Service -Name $webSvc
Get-Service -Name $webSvc
```

---

## 8. Configure Windows Firewall

The services listen only on localhost. Expose them publicly through IIS (step 9) on port **443** (HTTPS).

If you skip IIS and want to expose the ports directly (development only):

```powershell
# API port — for endpoint agents
New-NetFirewallRule -DisplayName "Raizen API" `
    -Direction Inbound -Protocol TCP -LocalPort 5001 -Action Allow

# Admin Web port — for approvers
New-NetFirewallRule -DisplayName "Raizen Admin Web" `
    -Direction Inbound -Protocol TCP -LocalPort 5002 -Action Allow
```

> For production: only open port **443** (handled by IIS in the next step).

---

## 9. Set Up IIS as a Reverse Proxy (Recommended)

Using IIS gives you TLS termination, certificate management, and access logging.

### 9.1 Install IIS and Required Modules

Run in an elevated PowerShell:

```powershell
# Install IIS with common features
Install-WindowsFeature -Name Web-Server, Web-Mgmt-Console, Web-Asp-Net45 `
    -IncludeManagementTools

# Download and install the .NET Hosting Bundle (includes IIS integration)
# Get the latest from: https://dotnet.microsoft.com/en-us/download/dotnet/8.0
# → "Hosting Bundle" → Windows
# Example:
Start-Process "dotnet-hosting-8.x.x-win.exe" -ArgumentList "/quiet" -Wait

# Install URL Rewrite module
# Download from: https://www.iis.net/downloads/microsoft/url-rewrite
# Install ARR (Application Request Routing) for reverse proxy
# Download from: https://www.iis.net/downloads/microsoft/application-request-routing
```

Enable the reverse proxy feature in ARR:
1. Open **IIS Manager**
2. Click the server node → **Application Request Routing Cache**
3. Right pane → **Server Proxy Settings**
4. Check **Enable proxy** → **Apply**

### 9.2 Obtain a TLS Certificate

**Option A — Windows Certificate Authority (domain-joined servers):**
```powershell
# Request a cert from your internal CA
$cert = Get-Certificate -Template WebServer `
    -DnsName "raizen.contoso.com" `
    -CertStoreLocation Cert:\LocalMachine\My
```

**Option B — Self-signed (development/pilot only):**
```powershell
$cert = New-SelfSignedCertificate `
    -DnsName "raizen.contoso.com" `
    -CertStoreLocation Cert:\LocalMachine\My `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -NotAfter (Get-Date).AddYears(2)
Write-Host "Thumbprint: $($cert.Thumbprint)"
```

### 9.3 Create IIS Sites

Open **IIS Manager** and create two sites (or use PowerShell):

```powershell
Import-Module WebAdministration

# ── Raizen API site ────────────────────────────────────────────────
New-WebSite -Name "RaizenAPI" `
    -Port 443 `
    -HostHeader "raizen-api.contoso.com" `
    -PhysicalPath "C:\inetpub\raizen-api" `
    -Ssl

# Add the TLS binding with your cert thumbprint
$certThumb = "YOUR_CERT_THUMBPRINT"
$store = "My"
netsh http add sslcert hostnameport="raizen-api.contoso.com:443" `
    certhash=$certThumb `
    certstorename=$store `
    appid='{00000000-0000-0000-0000-000000000001}'

# ── Raizen Admin Web site ──────────────────────────────────────────
New-WebSite -Name "RaizenWeb" `
    -Port 443 `
    -HostHeader "raizen.contoso.com" `
    -PhysicalPath "C:\inetpub\raizen-web" `
    -Ssl

netsh http add sslcert hostnameport="raizen.contoso.com:443" `
    certhash=$certThumb `
    certstorename=$store `
    appid='{00000000-0000-0000-0000-000000000002}'
```

Create placeholder directories:
```powershell
New-Item -ItemType Directory -Path C:\inetpub\raizen-api  -Force
New-Item -ItemType Directory -Path C:\inetpub\raizen-web  -Force
```

### 9.4 Add URL Rewrite / Reverse Proxy Rules

Create `C:\inetpub\raizen-api\web.config`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="ReverseProxy-API" stopProcessing="true">
          <match url="(.*)" />
          <action type="Rewrite" url="http://localhost:5001/{R:1}" />
        </rule>
      </rules>
    </rewrite>
    <security>
      <requestFiltering>
        <requestLimits maxAllowedContentLength="104857600" /> <!-- 100 MB for MSI uploads -->
      </requestFiltering>
    </security>
  </system.webServer>
</configuration>
```

Create `C:\inetpub\raizen-web\web.config`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="ReverseProxy-Web" stopProcessing="true">
          <match url="(.*)" />
          <action type="Rewrite" url="http://localhost:5002/{R:1}" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
```

### 9.5 Update the Entra ID Redirect URIs

Go back to your **Raizen-Web** app registration in Entra ID and update the redirect URIs to use your FQDN:
- `https://raizen.contoso.com/signin-oidc`
- `https://raizen.contoso.com/signout-callback-oidc`

---

## 10. Run the Database Migration

EF Core runs migrations automatically on startup. Verify they ran:

```powershell
# Check that the service started and tables were created
& "C:\Program Files\PostgreSQL\16\bin\psql.exe" -U raizen -d raizen -c "\dt"
```

You should see: `action_definitions`, `audit_logs`, `elevation_requests`, `endpoint_registrations`.

If you prefer to run the SQL migration manually instead:
```powershell
& "C:\Program Files\PostgreSQL\16\bin\psql.exe" `
    -U raizen -d raizen `
    -f "C:\Build\Raizen\scripts\db\V1__initial_schema.sql"
```

---

## 11. Create the First Admin Account & Roles

Assign your Entra ID user to the `Raizen.Admin` app role:

1. **Entra ID → Enterprise Applications → Raizen-API**
2. **Users and groups → Add user/group**
3. Select your admin user → Role: **Raizen Admin** → **Assign**

> Role assignments take up to 5 minutes to propagate. If you get a 403, wait and try again.

---

## 12. Verify the Installation

### 12.1 Check Windows Services

```powershell
Get-Service -Name RaizenAPI, RaizenWeb | Select-Object Name, Status, StartType
```
Both should show `Running`.

### 12.2 Check the API is Responding

```powershell
# Should return HTTP 401 (Unauthorized) — proves the API is up
Invoke-WebRequest -Uri "http://localhost:5001/api/v1/actions" -UseBasicParsing
```

### 12.3 Open the Admin Portal

Navigate to `https://raizen.contoso.com` in a browser.
You should be redirected to Microsoft's login page → sign in with your admin account → land on the Dashboard.

### 12.4 Check the Event Log

```powershell
Get-EventLog -LogName Application -Source "Raizen*" -Newest 20
```

---

## 13. Hardening Checklist

| Item | Action |
|------|--------|
| TLS 1.0 / 1.1 disabled | Run `IIS Crypto` (Nartac) or Group Policy |
| PostgreSQL not internet-exposed | Firewall rule: block 5432 from all external IPs |
| `appsettings.json` protected | `icacls "C:\Raizen\API\appsettings.json" /inheritance:r /grant "raizen-svc:R" /grant "Administrators:F"` |
| Audit log append-only | Run the `REVOKE DELETE, UPDATE ON audit_logs FROM raizen` SQL statement |
| Service account has minimal rights | No local admin membership; only "Log on as a service" |
| API key hashes only | Verify `ApiKeyHash` column in DB contains hex strings, not raw keys |
| Windows Updates current | Ensure all security patches applied |
| Antivirus exclusion for DB data | Exclude `C:\Program Files\PostgreSQL\16\data\` from real-time scan |

---

## Service Management Quick Reference

```powershell
# Start / Stop / Restart
Start-Service   RaizenAPI, RaizenWeb
Stop-Service    RaizenAPI, RaizenWeb
Restart-Service RaizenAPI, RaizenWeb

# View live logs
Get-Content C:\Raizen\API\logs\raizen-api-*.log -Tail 50 -Wait
Get-Content C:\Raizen\Web\logs\raizen-web-*.log -Tail 50 -Wait

# View Windows Event Log entries
Get-EventLog -LogName Application -Source "Raizen*" -Newest 50 | Format-List
```

---

*Raizen Server Installation Guide — v1.0*
