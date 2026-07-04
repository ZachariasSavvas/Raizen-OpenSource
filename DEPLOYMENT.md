# Raizen Security — Deployment Guide

---

## Windows Server (recommended, air-gapped compatible)

### What you'll need

| Requirement | Minimum |
|---|---|
| OS | Windows Server 2019 or 2022 |
| RAM | 4 GB |
| Disk | 40 GB |
| Account | Local Administrator |
| Open ports | 443 or 5002 (admin portal), 5001 (endpoint API) |
| Internet | **Not required** — fully air-gapped supported |

---

### Step 1 — Build the deployment package

On your development machine:

```powershell
.\scripts\Build-WindowsServer.ps1
```

This produces `release\RaizenServer-1.0.0\` containing:

```
RaizenServer-Setup.exe    ← setup wizard (run this on the server)
api\                      ← self-contained API binaries
web\                      ← self-contained Admin Portal binaries
agent\
    RaizenEndpoint.msi    ← endpoint installer for workstations
postgres\                 ← drop PostgreSQL installer here (air-gapped)
README.txt
```

---

### Step 2 — Prepare PostgreSQL (air-gapped)

The setup wizard can install PostgreSQL automatically from a bundled installer.

1. Download the PostgreSQL 16 Windows installer from
   `https://www.enterprisedb.com/downloads/postgres-postgresql-downloads`
   on any machine with internet access.

2. Copy `postgresql-16.x-windows-x64.exe` into the `postgres\` folder
   inside the deployment package.

> If PostgreSQL is already installed on the server, skip this step —
> the wizard will detect it automatically.

---

### Step 3 — Copy the package to the server

Transfer the entire `release\RaizenServer-1.0.0\` folder to the Windows Server
by any means: USB drive, network share, SFTP, etc.

---

### Step 4 — Run the setup wizard

1. Right-click `RaizenServer-Setup.exe` → **Run as administrator**

2. The wizard guides you through 6 steps:

   **Welcome** — Overview and licence agreement

   **Prerequisites** — Detects PostgreSQL; installs automatically from
   the `postgres\` folder if not found

   **Database** — Enter the PostgreSQL superuser password and the
   application database password (auto-generated, you can change it)

   **Server Configuration** — Set:
   - **Server hostname / IP** — the name or IP address endpoints will use
     to reach this server (e.g. `raizen-srv.corp.local` or `192.168.1.10`)
   - API port (default 5001)
   - Admin portal port (default 5002)
   - Encryption key (auto-generated — **write this down**)

   **Installing** — Runs automatically:
   - Sets up the database and user
   - **Generates an RSA-2048 key pair** for poll signing
   - **Generates a self-signed TLS certificate** (valid 5 years) for the
     hostname you entered and installs it to the Windows certificate store
   - Copies binaries to `C:\Program Files\Raizen\Server\`
   - Installs `RaizenApi` and `RaizenWeb` as Windows Services
   - Creates Windows Firewall inbound rules for both ports

   **Done** — Shows:
   - Admin portal URL: `https://<hostname>:5002`
   - TLS certificate thumbprint
   - Encryption key reminder

3. Click **Open Admin Portal** — your browser opens the login page.

   > If the browser shows a certificate warning, that is expected for a
   > self-signed cert. Click **Advanced → Proceed** to continue.
   > See Step 7 to distribute trust to endpoints.

---

### Step 5 — First login

| Field | Value |
|---|---|
| Username | `Admin` |
| Password | `Admin` |

You will be forced to change this password immediately. Choose a strong password.

**Recommended first steps in the portal:**
- **Settings** → configure SMTP for email notifications
- **Action Catalog** → enable the action types your organisation needs
- **Auto-Approval Rules** → set up rules for low-risk actions

---

### Step 6 — Deploy the endpoint to workstations

Copy `agent\RaizenEndpoint.msi` to each workstation and run it as Administrator.
The installer:
- Shows the Raizen Security EULA
- Installs the Windows service and tray application
- Creates Windows Firewall outbound rules
- Sets up right-click context menus on files and folders

After install, edit `%ProgramData%\Raizen\raizen-config.json` on each machine:

```json
{
  "ServerUrl": "https://raizen-srv.corp.local:5001",
  "TlsPinThumbprint": "<cert thumbprint from Done screen>",
  "MachineId": "AUTO_GENERATED_ON_FIRST_RUN",
  "ApiKey": "SET_DURING_INSTALLATION"
}
```

> **TlsPinThumbprint** — paste the thumbprint from the Done screen.
> This pins the endpoint to your server's self-signed cert so it
> won't connect to anything else. Leave blank to use the Windows cert
> store (recommended if you've distributed the cert via GPO).

**Or use GPO / SCCM to deploy the MSI silently:**
```
msiexec /i RaizenEndpoint.msi /qn
```
Then push `raizen-config.json` via GPO preferences or a startup script.

---

### Step 7 — Distribute TLS trust (self-signed cert)

For endpoints to trust the server's self-signed certificate without
a `TlsPinThumbprint`, deploy the cert to the machine trust store via GPO:

1. Export the cert from the server:
   - Open **certlm.msc** (Local Computer certificates)
   - Navigate to **Trusted Root Certification Authorities → Certificates**
   - Find "Raizen Security" → right-click → **Export** → DER encoded (.cer)

2. Deploy via GPO:
   - Computer Configuration → Windows Settings → Security Settings →
     **Public Key Policies → Trusted Root Certification Authorities**
   - Import the `.cer` file

---

### Step 8 — Register each endpoint on the server

After installing the endpoint agent on a workstation:

1. The service logs its **Machine ID** and **API Key** to
   `%ProgramData%\Raizen\Logs\` on first start.

2. In the admin portal → **Endpoints** → **Register Endpoint**:
   - Enter the Machine ID and API Key
   - Give the machine a friendly name

3. The endpoint shows **Online** within 30 seconds.

---

### Step 9 — End-to-end test

1. On a registered workstation, right-click any `.exe` → **Raizen** →
   **Request to Run as Admin**
2. Fill in the reason and submit
3. In the admin portal → **Requests** — request appears as **Pending**
4. Click the request → **Approve**
5. The tray app shows a notification: "Elevation Approved"
6. The service executes the action within 30 seconds (one poll cycle)

---

### Firewall reference

Open these ports on the server's network / host firewall:

| Port | Direction | Purpose |
|---|---|---|
| 5001 | Inbound TCP | Endpoint API (HTTPS) |
| 5002 | Inbound TCP | Admin portal (HTTPS) |

Workstations only need **outbound** TCP to ports 5001 on the server.

---

### Upgrading to a new version

1. Build the new deployment package:
   ```powershell
   .\scripts\Build-WindowsServer.ps1 -Version "1.1.0"
   ```

2. Transfer to the server and run `RaizenServer-Setup.exe` as Administrator.
   The wizard detects the existing installation and updates the binaries
   and services in-place. Your config, database, and certificates are preserved.

3. If the release notes include a DB schema change, run the migration
   script **before** starting the wizard:
   ```sql
   -- example: release\RaizenServer-1.1.0\db-upgrades\V2__description.sql
   psql -U raizen -d raizen -f V2__description.sql
   ```

**Endpoint (MSI):** Run the new `RaizenEndpoint.msi` on each workstation
(or push via GPO/SCCM). Windows Installer detects the previous version and
upgrades it automatically. Config in `%ProgramData%\Raizen\` is preserved.

---

### Useful service commands

```powershell
# Check service status
Get-Service RaizenApi, RaizenWeb

# Restart services
Restart-Service RaizenApi
Restart-Service RaizenWeb

# View logs
Get-Content "C:\Program Files\Raizen\Server\Api\logs\raizen-api-*.log" -Tail 50
Get-Content "C:\Program Files\Raizen\Server\Web\logs\raizen-web-*.log" -Tail 50
```

---

### Troubleshooting

| Problem | Check |
|---|---|
| Setup wizard fails on Prerequisites | Is PostgreSQL installed? Is the service named `postgresql-*`? |
| Can't reach admin portal | Is `RaizenWeb` service running? Is port 5002 open in firewall? |
| Browser cert warning | Expected for self-signed cert — click Advanced → Proceed, or deploy cert via GPO |
| Endpoints show Offline | Is `ServerUrl` in `raizen-config.json` correct and reachable? Port 5001 open? |
| Login fails after upgrade | Encryption key in `appsettings.Production.json` must stay the same |
| Service won't start | Check Windows Event Log → Application → Source: `RaizenApi` / `RaizenWeb` |
| Requests not executing | Is `RaizenEndpoint` service running as SYSTEM? Check endpoint logs in `%ProgramData%\Raizen\Logs\` |
| Email notifications not working | Configure SMTP in portal → Settings → Notifications |

---

## Linux / Docker deployment (internet-connected servers)

If you prefer Docker on Linux, see the `docker-compose.yml` and `scripts/deploy-server.sh`.

```bash
# On the Linux server
git clone https://github.com/your-org/Raizen.git /opt/raizen
cd /opt/raizen
bash scripts/deploy-server.sh    # creates .env, generates cert, starts stack
```

The Docker stack exposes the same ports (443 for admin, 5001 for endpoints)
via an nginx reverse proxy and requires the same endpoint MSI on workstations.
