# Raizen v1.0.14 — Test Plan

This document covers all changes shipped in v1.0.14 with step-by-step verification procedures,
expected outcomes, and pass/fail criteria. Each section maps to a discrete change.

---

## 1. Em Dash Removal (UI + Email)

### Background
All em dashes (`—`) were removed from user-facing text to improve readability.
Subjects use `:`, page titles use `|`, inline prose uses `,`/`;`.

### 1.1 Email Subjects

**Precondition:** SMTP configured in Settings, a valid recipient address.

| Test | Action | Expected Subject | Pass? |
|------|--------|-----------------|-------|
| Test email | Settings > Notifications > Send Test | `Raizen: Test Email` | |
| Approval notification | Submit an elevation request (auto-notify on) | `[Raizen] Approval Required: <action> on <machine>` | |
| Approved notification | Approve a pending request | `[Raizen] Request Approved: <action> on <machine>` | |
| Denied notification | Deny a pending request | `[Raizen] Request Denied: <action> on <machine>` | |

**Check:** Open received emails in any mail client. No `—` character should appear in the subject line.

### 1.2 Page Titles (Browser Tab)

Navigate to each page and check the browser tab title:

| Page | Expected Title |
|------|---------------|
| Dashboard | `Dashboard \| Raizen` |
| Endpoints | `Endpoints \| Raizen` |
| Pending Approvals | `Pending Approvals \| Raizen` |
| Requests | `Requests \| Raizen` |
| Request Detail | `Request Detail \| Raizen` |
| Action Catalog | `Action Catalog \| Raizen` |
| Auto-Approval Rules | `Auto-Approval Rules \| Raizen` |
| Audit Log | `Audit Log \| Raizen` |
| Notifications | `Notifications \| Raizen` |
| Settings | `Settings \| Raizen` |
| License | `License \| Raizen` |
| Agent Deployment | `Agent Deployment \| Raizen` |

**Pass criterion:** Every tab shows `| Raizen` suffix, no `—` character anywhere.

---

## 2. Endpoint Status Auto-Refresh

### Background
The Endpoints page used to show stale offline/online status until manually refreshed.
A 30-second countdown timer now auto-reloads the endpoint list.

### Test Steps

1. Open the admin portal > Endpoints page.
2. Observe the countdown indicator in the top-right of the endpoint list header
   (should read "Refreshes in 30 s" initially).
3. Wait 30 seconds without interacting.
4. The endpoint list reloads automatically; countdown resets to 30.

**Trigger test (offline detection):**

1. Stop the `RaizenEndpoint` Windows Service on a test machine.
2. Wait up to 2 minutes for the heartbeat to lapse (heartbeat interval is 60 s + server timeout).
3. Without refreshing the browser, wait for the auto-refresh cycle.
4. The endpoint's status changes from `Online` to `Offline` automatically.

**Pass criterion:**
- Countdown visible and counts down correctly.
- Endpoint status updates without manual page refresh.
- No JavaScript errors in browser console.

---

## 3. Audit Log HMAC Integrity Fix

### Background
On fresh PostgreSQL installs the Integrity Check always showed a warning because
`DateTimeOffset.UtcNow` (100 ns precision) differs from PostgreSQL `timestamptz`
(microsecond precision) on readback, breaking the HMAC chain.

### Test Steps

**New installation:**

1. Install Raizen server from scratch (fresh DB).
2. Perform several actions: log in, approve a request, change a setting.
3. Navigate to Audit Log > Integrity Check.
4. Expected: all entries show green / verified. No warning banner.

**Existing installation (entries written before v1.0.14):**

Pre-v1.0.14 entries were hashed with un-truncated timestamps. They will still
show as invalid in the HMAC chain. This is expected — the chain restarts cleanly
from the first v1.0.14-written entry. Document the cut-over entry ID in deployment notes.

**Pass criterion:**
- Zero integrity warnings for entries written after upgrading to v1.0.14.
- Warning count matches the count of entries written before the upgrade (acceptable).

---

## 4. Agent Banner Text Rendering (GDI+ Fix)

### Background
On laptops with non-standard DPI settings, the colored banner text in tray forms
appeared squished or had inconsistent character spacing. Fixed by setting
`TextRenderingHint.ClearTypeGridFit` before `DrawString`.

### Affected Forms

| Form | How to Open |
|------|------------|
| Run As Admin | Tray icon > right-click file > Raizen > Run As Admin |
| File Transfer | Tray icon > right-click file/folder > Raizen > Request File Transfer |
| File Permissions | Tray icon > right-click > Raizen > Set File/Folder Permissions |
| Services | Tray icon > Raizen > Services |
| Env Var | Tray icon > Raizen > Environment Variable |
| Network Change | Tray icon > Raizen > Network Configuration |

### Test Steps

1. On a machine with a non-standard DPI (125%, 150%, or 175%), open each form listed above.
2. Inspect the colored banner area at the top of each form.
3. Text should render with consistent spacing — no glyph bunching or overlap.

**Pass criterion:**
- Banner text is clearly legible at all tested DPI settings.
- No character spacing anomalies visible.

---

## 5. "Request File Transfer" in Folder Context Menu

### Background
Right-clicking a folder and selecting Raizen did not show "Request File Transfer"
— it only appeared on individual files. The `$dirVerbs` arrays in all 4 PowerShell
scripts were missing the File Transfer entry.

### Test Steps

1. Re-run `Install-RaizenEndpoint.ps1` (or `Repair-RaizenContextMenus.ps1`) as Administrator.
2. After script completes, right-click a **folder** in Explorer.
3. Expand the Raizen submenu.

| Scenario | Entry visible | Opens correct form |
|----------|--------------|-------------------|
| Right-click any folder | `Request File Transfer` | FileTransferForm with 📁 source icon |
| Right-click any file | `Request File Transfer` | FileTransferForm with 📄 source icon |
| Right-click executable (.exe) | `Request File Transfer` | FileTransferForm with 📄 source icon |
| Right-click Desktop background | `Request File Transfer` | FileTransferForm |

4. Open the form from a folder path. Verify the source chip shows 📁 (folder icon).
5. Open the form from a file path. Verify the source chip shows 📄 (file icon).

**Pass criterion:**
- "Request File Transfer" visible in context menu for folders, files, and desktop background.
- Correct icon displayed in the FileTransferForm source chip.

---

## 6. Audit Log Excel Export

### Background
The previous JSON export via JS interop was broken. Replaced with an Excel (`.xlsx`)
download served by an MVC controller.

### Test Steps

1. Navigate to Audit Log.
2. (Optional) Apply filters: Event Type, User, Date From, Date To.
3. Click **Export Excel**.
4. Browser downloads a file named `audit-YYYY-MM-DD.xlsx`.

**Workbook verification:**

| Check | Expected |
|-------|----------|
| Sheet 1 name | `Audit Log` |
| Sheet 2 name | `Export Info` |
| Header row | Navy background, white bold text |
| Columns | Time (UTC), Event, Actor UPN, Requester UPN, Approved By, Machine, IP Address, Detail, Request ID |
| Auto-filter | Filter dropdowns on header row |
| Alternating rows | Light grey shading on even rows |
| Column widths | Auto-fitted, no truncated content (max 60 chars) |
| Export Info sheet | Shows applied filters, timestamp, record count |

**Filter test:**

1. Filter by a specific user UPN.
2. Export Excel.
3. Open file — only rows matching that user should be present.
4. "Export Info" sheet should show the applied user filter.

**Pass criterion:**
- File downloads without error.
- All columns present and populated correctly.
- Filters applied correctly in the exported data.
- No 500 error in server logs.

---

## 7. Favicon on Admin Portal

### Background
The Raizen `.ico` file was added as the browser favicon for the admin portal.

### Test Steps

1. Open the admin portal in a browser.
2. Check the browser tab icon.
3. Add the portal to bookmarks — the bookmark icon should use the Raizen favicon.

**Pass criterion:**
- The Raizen logo (not the default browser/Blazor icon) appears in the browser tab.
- Correct icon visible in bookmarks.

---

## 8. Agent Auto-Update System

### Background
Agents now poll the server every 4 hours for a newer MSI. When the server version
is higher than the locally installed version, the agent downloads the MSI and
silently applies it via `msiexec`.

### 8.1 Server Configuration

1. Build the new agent MSI and place it at the path configured in `AgentDeployment:InstallerPath`.
2. Open `appsettings.Production.json` on the Web server and set:
   ```json
   "AgentDeployment": {
     "InstallerPath": "C:\\Raizen\\agent\\RaizenEndpoint.msi",
     "CurrentVersion": "1.0.14"
   }
   ```
3. Restart the Raizen Web service.

### 8.2 Admin Portal Status Panel

1. Navigate to Agent Deployment.
2. Verify the Auto-Update status panel at the top of the card shows:
   - Current release: `1.0.14`
   - Count of up-to-date endpoints (green badge)
   - Count of outdated endpoints (yellow badge)
   - Count of never-reported endpoints (grey badge)
   - If MSI file not found at path: red warning triangle

**Test: MSI missing warning:**
1. Set `InstallerPath` to a non-existent path.
2. Reload the page. The red "MSI not found" warning should appear next to the version.

### 8.3 Agent Self-Update (Endpoint Side)

**Precondition:** At least one endpoint running an older agent version (e.g. 1.0.13).

1. Set `CurrentVersion` on the server to `1.0.14` as above.
2. On the endpoint, check the service log at `%ProgramData%\Raizen\Logs\service-*.log`.
3. Within 4 hours (or restart the service to trigger immediately after the 60 s startup delay):
   - Log shows: `Update available: 1.0.13 -> 1.0.14. Downloading MSI...`
   - Log shows: `Launched msiexec to apply update to 1.0.14.`
4. After msiexec completes (typically 1-2 minutes):
   - Service restarts automatically.
   - Log shows: `Raizen Endpoint Service starting.` with the new version.

**Force early check (for testing):**
- Restart the `RaizenEndpoint` Windows Service. The check runs 60 seconds after startup.

**API endpoint tests:**

| Endpoint | Expected when configured | Expected when not configured |
|----------|------------------------|------------------------------|
| `GET /api/v1/agent/version` (with valid ApiKey) | `{ "version": "1.0.14" }` | `404 Not Found` |
| `GET /api/v1/agent/msi` (with valid ApiKey) | MSI binary stream | `404 Not Found` |
| `GET /api/v1/agent/version` (no auth) | `401 Unauthorized` | `401 Unauthorized` |

**Pass criterion:**
- Outdated endpoints self-update within one check cycle (4 hours max, or on service restart).
- No user interaction required on the endpoint.
- msiexec log at `%TEMP%\raizen-update.log` shows a successful installation.
- Admin portal version badges update after the next heartbeat from the endpoint.

### 8.4 Version Already Current

1. With `CurrentVersion = 1.0.14` on the server and a `1.0.14` agent on the endpoint:
2. Check service log after 4 hours.
3. Expected: `Agent is up to date (local=1.0.14, server=1.0.14).` — no MSI downloaded.

### 8.5 Auto-Update Disabled

1. Remove or leave blank `AgentDeployment:CurrentVersion` in `appsettings.Production.json`.
2. `GET /api/v1/agent/version` returns `404`.
3. Endpoint log shows no update-related entries.
4. Admin portal Auto-Update panel shows the "Not configured" instructional message.

---

## 9. Regression — Core Functionality

Verify no regressions in features not directly changed by v1.0.14:

| Feature | Check |
|---------|-------|
| Login / Logout | Local cookie auth works; wrong password rejected |
| Change password | New password accepted; old password rejected after change |
| Submit elevation request (Tray) | Request appears in Pending Approvals |
| Approve request | Endpoint executes action; status moves to Approved |
| Deny request | Endpoint receives denial; status moves to Denied |
| Auto-approval rules | Matching request auto-approved without manual intervention |
| Action catalog | Create / edit / delete action definitions |
| Endpoint registration | Fresh Windows machine installs MSI, registers via token |
| Heartbeat | Online/Offline status updates within 2 minutes |
| Offline queue | Action queued while endpoint offline; executed on reconnect |
| Notifications | Email sent on approval/denial if configured |
| Audit log | All actions logged; HMAC chain intact for new entries |
| License page | License status displayed (trial or licensed) |

---

## 10. Build Verification

Run before every release:

```bash
dotnet build src/Raizen.Server/Raizen.Server.Api  -c Release  # 0 errors
dotnet build src/Raizen.Server/Raizen.Server.Web  -c Release  # 0 errors
dotnet build src/Raizen.Endpoint/Raizen.Endpoint.Service -c Release  # 0 errors
dotnet build src/Raizen.Endpoint/Raizen.Endpoint.Tray    -c Release  # 0 errors
```

All four must report `Build succeeded. 0 Error(s)`.

**Release package:**

```powershell
.\scripts\Build-WindowsServer.ps1 -Version "1.0.14"
```

Verify `release\RaizenServer-1.0.14\` contains:
- `RaizenServer-Setup.exe`
- `api\` — populated
- `web\` — populated (includes `wwwroot\favicon.ico`)
- `agent\RaizenEndpoint.msi` — if MSI was built
- `README.txt`

---

## Sign-Off

| Area | Tester | Date | Result |
|------|--------|------|--------|
| Em dash removal | | | |
| Page titles | | | |
| Endpoint auto-refresh | | | |
| Audit HMAC integrity | | | |
| Banner text rendering | | | |
| Folder context menu | | | |
| Excel export | | | |
| Favicon | | | |
| Agent auto-update | | | |
| Regression suite | | | |
