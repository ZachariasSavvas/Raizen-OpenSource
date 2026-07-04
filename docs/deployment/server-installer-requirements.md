# Raizen Server Deployment Package — Requirements

**Document type:** Deployment package functional specification
**Package format:** ZIP archive + PowerShell setup script
**Target platform:** Windows Server 2019 or 2022 (x64)
**Components deployed:** PostgreSQL 16, Raizen API (Windows Service), Raizen Web (Windows Service), IIS reverse proxy

---

## 1. Components

| Component | Technology | Runs as | Default port | Purpose |
|---|---|---|---|---|
| PostgreSQL | PostgreSQL 16 for Windows | NT Service (`postgresql-x64-16`) | 5432 (localhost only) | Persistent data store |
| Raizen API | ASP.NET Core 8, Windows Service | `NT AUTHORITY\NetworkService` | 5001 (localhost) | REST API for endpoints and admin |
| Raizen Web | ASP.NET Core 8, Windows Service | `NT AUTHORITY\NetworkService` | 5002 (localhost) | Admin web portal (Blazor Server) |
| Reverse proxy | IIS 10 + ARR + URL Rewrite | IIS | 443 (HTTPS) | TLS termination; routes subdomains to API and Web |

---

## 2. Deployment Package Contents

```
raizen-server-<version>.zip
  api\                         — pre-built Raizen.Server.Api publish output
  web\                         — pre-built Raizen.Server.Web publish output
  scripts\
    Install-RaizenServer.ps1   — interactive first-run setup script
    Uninstall-RaizenServer.ps1 — full teardown
    Backup-RaizenDb.ps1        — pg_dump to backup folder
    Restore-RaizenDb.ps1       — restore from dump
  iis\
    raizen-api.conf            — IIS ARR reverse proxy site config (XML)
    raizen-web.conf            — IIS ARR reverse proxy site config (XML)
  sql\
    V1__initial_schema.sql     — database schema (applied automatically by API on first start)
```

---

## 3. Infrastructure Prerequisites

### 3.1 Host machine

| Requirement | Minimum | Recommended |
|---|---|---|
| OS | Windows Server 2019 (build 17763) | Windows Server 2022 |
| CPU | 2 vCPU | 4 vCPU |
| RAM | 4 GB | 8 GB |
| Disk | 40 GB | 100 GB (for DB growth, logs, backups) |
| .NET | .NET 8 ASP.NET Core Runtime (x64) | Same |
| IIS | Installed with ARR + URL Rewrite modules | Same |
| PowerShell | 5.1 or later | 7.x |

### 3.2 DNS records (must exist before install)

| Hostname | Type | Value |
|---|---|---|
| `api.raizen.contoso.com` | A | Server IP |
| `admin.raizen.contoso.com` | A | Server IP |

### 3.3 TLS certificate

A certificate covering both hostnames is required. Accepted formats:
- PFX (`.pfx` / `.p12`) with private key — **preferred** for IIS import
- PEM + key — converted to PFX by setup script

Sources:
- Internal PKI / CA (recommended for on-premise)
- Let's Encrypt via `win-acme` (for internet-facing servers)
- Self-signed — lab/testing only

---

## 4. Required Configuration Values

The setup script collects the following. All values are written to:
- `%ProgramFiles%\Raizen\Api\appsettings.Production.json`
- `%ProgramFiles%\Raizen\Web\appsettings.Production.json`

| Value | Where entered | Notes |
|---|---|---|
| `ServerUrl` (API external URL) | Setup script prompt | e.g. `https://api.raizen.contoso.com` |
| `WebUrl` (Web external URL) | Setup script prompt | e.g. `https://admin.raizen.contoso.com` |
| `PostgresPassword` | Setup script prompt | Min 16 chars; used only on localhost |
| `WebEncryptionKey` | Setup script prompt or auto-generated | 32+ char random string; AES-256-GCM for admin passwords — **back up this value** |
| TLS certificate path | Setup script prompt | Path to `.pfx` file |
| TLS certificate password | Setup script prompt | PFX password; may be empty |

---

## 5. Files Installed

```
%ProgramFiles%\Raizen\
  Api\
    Raizen.Server.Api.exe             (and all .dll dependencies)
    appsettings.json                  (base config; do not edit)
    appsettings.Production.json       (written by setup script; contains secrets)
  Web\
    Raizen.Server.Web.exe             (and all .dll dependencies)
    appsettings.json                  (base config; do not edit)
    appsettings.Production.json       (written by setup script; contains secrets)

%ProgramData%\Raizen\Server\
  Backups\                            (database dumps; admin-only access)
  Logs\
    Api\                              (API rolling log files)
    Web\                              (Web rolling log files)
```

### 5.1 `appsettings.Production.json` — API

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=raizen;Username=raizen;Password=<PostgresPassword>"
  },
  "AllowedHosts": "api.raizen.contoso.com"
}
```

### 5.2 `appsettings.Production.json` — Web

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=raizen;Username=raizen;Password=<PostgresPassword>"
  },
  "Security": {
    "EncryptionKey": "<WebEncryptionKey>"
  },
  "RaizenApi": {
    "BaseUrl": "http://localhost:5001"
  },
  "AllowedHosts": "admin.raizen.contoso.com"
}
```

Both files must be readable only by the service account (`NetworkService`) and `BUILTIN\Administrators`. Regular users must not be able to read them.

---

## 6. Database Setup

The setup script must:

1. Install PostgreSQL 16 silently (if not already installed)
2. Create the `raizen` database
3. Create the `raizen` database user with a strong password
4. Grant the `raizen` user all privileges on the `raizen` database
5. Configure PostgreSQL to listen on `127.0.0.1` only (`pg_hba.conf`: `host raizen raizen 127.0.0.1/32 scram-sha-256`)

The API applies the schema automatically using EF Core on first start. No manual SQL execution is required.

---

## 7. Windows Services

### 7.1 Raizen API Service

| Property | Value |
|---|---|
| Service name | `RaizenApi` |
| Display name | Raizen API |
| Description | Raizen elevation platform REST API. |
| Binary path | `"%ProgramFiles%\Raizen\Api\Raizen.Server.Api.exe"` |
| Environment | `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_URLS=http://localhost:5001` |
| Account | `NT AUTHORITY\NetworkService` |
| Start type | Automatic (Delayed Start) |
| Failure recovery | Restart after 10 s (×3), then wait 60 s |

### 7.2 Raizen Web Service

| Property | Value |
|---|---|
| Service name | `RaizenWeb` |
| Display name | Raizen Web |
| Description | Raizen elevation platform admin portal. |
| Binary path | `"%ProgramFiles%\Raizen\Web\Raizen.Server.Web.exe"` |
| Environment | `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_URLS=http://localhost:5002` |
| Account | `NT AUTHORITY\NetworkService` |
| Start type | Automatic (Delayed Start) |
| Failure recovery | Restart after 10 s (×3), then wait 60 s |
| Dependency | `RaizenApi` (must start first) |

---

## 8. IIS Configuration

### 8.1 Prerequisites installed by setup script

- IIS role (`Web-Server`)
- ARR 3.0 (`Application Request Routing`)
- URL Rewrite 2.1
- IIS Management Tools

### 8.2 Sites created

| Site | Hostname | Binding | Upstream |
|---|---|---|---|
| `RaizenApi` | `api.raizen.contoso.com` | HTTPS :443 | `http://localhost:5001` |
| `RaizenWeb` | `admin.raizen.contoso.com` | HTTPS :443 | `http://localhost:5002` |

Each site is a simple ARR reverse proxy — IIS receives the HTTPS request, terminates TLS, and forwards plain HTTP to the local Kestrel process.

### 8.3 TLS certificate

The setup script imports the provided PFX into the `LocalMachine\My` certificate store and binds it to both IIS sites using SNI (one cert per hostname, or a SAN cert shared).

---

## 9. Upgrade Behaviour

1. Stop `RaizenApi` and `RaizenWeb` services
2. Replace binaries in `%ProgramFiles%\Raizen\Api\` and `Web\` (preserve `appsettings.Production.json`)
3. Start services — EF Core runs any new migrations automatically
4. No data loss; PostgreSQL data is untouched

---

## 10. Uninstall Behaviour

1. Stop and delete `RaizenApi` and `RaizenWeb` Windows services
2. Remove IIS sites and application pools
3. Remove binaries from `%ProgramFiles%\Raizen\`
4. Prompt: remove `%ProgramData%\Raizen\Server\` (logs, backups)? Default: No
5. Prompt: uninstall PostgreSQL and drop the `raizen` database? Default: No

---

## 11. Security Requirements

| Item | Requirement |
|---|---|
| TLS | TLS 1.2 minimum; TLS 1.3 preferred |
| `appsettings.Production.json` | ACL: `NetworkService` read; `Administrators` full; `Users` no access |
| PostgreSQL | Bind to `127.0.0.1` only; no remote access |
| `WebEncryptionKey` | Must be stored in IT's password vault; loss requires admin password reset |
| Default admin password | Portal enforces change on first login |
| Audit log | Append-only; DB user `raizen` has no DELETE on `audit_log` table |
| Log retention | 90-day retention; enforced by setup script scheduled task |
