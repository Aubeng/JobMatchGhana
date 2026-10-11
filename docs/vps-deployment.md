# Deploying to the Windows/IIS VPS (staging)

JobMatch runs on the shared Intellite VPS at **`https://jobmatch.intellite.net`** before it moves to
Azure App Service (DL-012). This gives Meta a permanent HTTPS webhook URL, so there's no dev tunnel
to keep alive.

| | |
|---|---|
| Server | Windows Server 2022 Datacenter, IIS 10 (shared with other sites) |
| Database | SQL Server 2022 Web Edition on the same box, default instance (`.`) |
| Sessions | Redis for Windows (tporadowski port), `localhost:6379` |
| App | ASP.NET Core 8, in-process IIS hosting, own app pool `JobMatch` |
| Webhook | `https://jobmatch.intellite.net/webhook` |
| Health | `https://jobmatch.intellite.net/health` → `{"status":"ok"}` |

```
 Meta WhatsApp Cloud API
          │  HTTPS POST /webhook (signed)
          ▼
 ┌─────────────────── VPS ───────────────────────────────────┐
 │ IIS  :443 jobmatch.intellite.net (Let's Encrypt, win-acme) │
 │  └─ app pool "JobMatch"  →  C:\inetpub\jobmatch\releases\<release>\app
 │        ├─ SQL Server (.)   DB JobMatchGhana, Windows auth as IIS APPPOOL\JobMatch
 │        └─ Redis  localhost:6379   keys jobmatch:session:*
 └────────────────────────────────────────────────────────────┘
```

Scripts live in [`deploy/`](../deploy). `Publish.ps1` runs on your dev machine; everything in
`deploy/server/` runs **on the VPS in an elevated PowerShell** and travels inside each release zip.

---

## 1. One-time server preparation

Do these once, in order.

### 1.1 DNS
At whoever hosts DNS for `intellite.net`, add an **A record** `jobmatch` → the VPS's public IP.
Check from any machine:
```powershell
Resolve-DnsName jobmatch.intellite.net
```

### 1.2 Patch SQL Server
The instance is at **RTM (16.0.1000.6)**, with no updates since October 2022. Install the latest
*Cumulative Update for SQL Server 2022* (Microsoft Update Catalog or the SQL Server "latest updates"
page) before real participants' data goes on it. Restart the SQL service afterwards. That briefly
affects the other sites' databases, so pick a quiet time.

### 1.3 .NET 8 Hosting Bundle
Download **ASP.NET Core Runtime 8.0.x – Windows Hosting Bundle** from
<https://dotnet.microsoft.com/download/dotnet/8.0> and install it, then restart IIS:
```powershell
net stop was /y
net start w3svc
```
(It installs side by side; other sites on other .NET versions are unaffected.)

### 1.4 Redis
The tporadowski service config is `C:\Program Files\Redis\redis.windows-service.conf`. Check:

- `bind 127.0.0.1`: Redis must not listen on the public interface.
- Port 6379 is **not** open in Windows Firewall.

A password (`requirepass`) is good hygiene, **but this Redis is shared**: if other sites already use
it without a password, adding one breaks them until their connection strings are updated too.
Bound to localhost only, it's acceptable to leave it password-less for staging. JobMatch's keys are
isolated by the `jobmatch:session:` prefix either way. After changing the config:
`Restart-Service Redis`.

### 1.5 Get the scripts onto the server
Build a release on your dev machine (section 2.1), copy the zip to the VPS (RDP copy-paste is
fine), and extract it once anywhere (e.g. `C:\Deploy\first`) to get the `server\` folder.

### 1.6 IIS site and app pool
```powershell
cd C:\Deploy\first\server
.\Setup-Server.ps1
```
This creates `C:\inetpub\jobmatch\{releases,logs}`, the **`JobMatch` app pool** (No Managed Code,
ApplicationPoolIdentity), the **`jobmatch` site** bound to `http://jobmatch.intellite.net:80`, and
grants the pool identity read access. It's idempotent, and it never touches other sites.
Parameters (`-HostName`, `-Root`, …) are documented at the top of the script.

### 1.7 Database
As a SQL sysadmin (your Windows admin account if it's sysadmin; otherwise add `-U sa` and drop `-E`):
```powershell
sqlcmd -S . -E -b -i .\create-database.sql -v DatabaseName="JobMatchGhana" AppPool="JobMatch"
```
This creates `JobMatchGhana` and a Windows login for `IIS APPPOOL\JobMatch` with read/write on that
database only. There is no SQL password anywhere. Schema changes are applied by
`Deploy-Release.ps1` under your admin account, so the app itself has no DDL rights.

> `sqlcmd` missing? `winget install sqlcmd`.

### 1.8 Settings and secrets
```powershell
.\Set-AppSettings.ps1
```
It prompts for the Redis password (blank if none) and the four WhatsApp values (App Secret,
verify token, phone number ID, access token). Secrets are typed hidden. Values are stored as
**environment variables on the `JobMatch` app pool** in IIS's `applicationHost.config`:
admin-readable only, never in the site folder, the repo or a release zip, and they survive
redeploys. Re-run it any time to change a value (Enter keeps the current one).

With the access token left blank the app runs in **dry-run mode**: replies are logged, not sent.
That's a safe way to do the first deploy.

> **Use a permanent access token here** (System User token, see `whatsapp-cloud-api-setup.md`).
> The 24-hour temporary token from the API Setup page will silently stop outbound messages.

### 1.9 HTTPS certificate (win-acme)
1. Download the x64 "pluggable" zip from <https://www.win-acme.com> and extract it to e.g.
   `C:\Tools\win-acme`.
2. Run `wacs.exe` as Administrator → **N** (create certificate, default settings) →
   **Single binding of an IIS site** → pick `jobmatch.intellite.net` → give a contact email.
3. win-acme validates over port 80, adds an **SNI** `https :443` binding for that host name only
   (other sites' certificates are untouched), and installs a scheduled task that renews it.

Check: `https://jobmatch.intellite.net/` shows the "No release deployed yet" holding page.

### 1.10 App pool: keep the queue alive ⚠️ (your reminder)

By default IIS **stops an idle app after 20 minutes and recycles it every 29 hours**. The webhook
replies 200 to Meta *before* processing (DL-002); queued messages live in memory. A recycle
drops any that weren't processed yet, so they're **lost from the research dataset**, and
Meta won't resend them because it already got its 200. An idle shutdown also makes the next
message wait for a cold start.

Set these on the **`JobMatch` pool only**:

| Setting | Where (IIS Manager) | Value |
|---|---|---|
| Start Mode | App Pools → JobMatch → Advanced Settings → General | `AlwaysRunning` |
| Idle Time-out (minutes) | … → Process Model | `0` |
| Regular Time Interval (minutes) | … → Recycling | `0` |
| Preload Enabled | Sites → jobmatch → Advanced Settings → General | `True` |

Preload needs the Application Initialization feature. Or do all of it from PowerShell:
```powershell
Install-WindowsFeature Web-AppInit
Import-Module WebAdministration
Set-ItemProperty IIS:\AppPools\JobMatch -Name startMode -Value AlwaysRunning
Set-ItemProperty IIS:\AppPools\JobMatch -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty IIS:\AppPools\JobMatch -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty IIS:\Sites\jobmatch -Name applicationDefaults.preloadEnabled -Value $true
```
A deploy still restarts the app once (by design). Do deploys when nobody is mid-conversation.

---

## 2. Every release

### 2.1 Build (dev machine)
```powershell
.\deploy\Publish.ps1
```
Runs the tests, publishes the API, generates an **idempotent** `migrate.sql` with the repo-pinned
`dotnet-ef` 8.0.8, and writes `artifacts\jobmatch-<timestamp>.zip`. `-SkipTests` skips the tests.

### 2.2 Deploy (VPS)
Copy the zip over, then:
```powershell
cd C:\inetpub\jobmatch                # or wherever a server\ folder is
.\server\Deploy-Release.ps1 -Package C:\Deploy\jobmatch-20261011-140000.zip
```
It unpacks into `releases\<name>`, applies `migrate.sql`, re-points the site at the new folder,
polls `/health`, and keeps the newest 5 releases. Nothing is overwritten in place, so there are
no locked-file errors. If the migration fails, the site keeps serving the old release.

Use the `server\` scripts from the **newest** zip, since they may have changed.

### 2.3 Roll back
```powershell
.\server\Deploy-Release.ps1 -Rollback
```
Points the site at the previous release. The database schema is **not** rolled back. Migrations
are applied before the switch, so keep them additive (add columns/tables; rename or drop in a
later release).

---

## 3. Connect Meta to the server

In the Meta developer dashboard → **WhatsApp → Configuration → Webhook** (full walkthrough in
[whatsapp-cloud-api-setup.md](whatsapp-cloud-api-setup.md)):

| Field | Value |
|---|---|
| Callback URL | `https://jobmatch.intellite.net/webhook` |
| Verify token | the same value you gave `Set-AppSettings.ps1` |
| Webhook fields | subscribe to `messages` |

Click **Verify and save**. Meta calls `GET /webhook` and the app echoes the challenge. This is a
one-off: the URL never changes again. Then message the business number from a test phone
(e.g. "hi") and you should get the consent prompt.

---

## 4. Troubleshooting

First stop for any failure: **Event Viewer → Windows Logs → Application**, sources
`IIS AspNetCore Module V2` and `.NET Runtime`. ASP.NET Core also logs its own warnings and errors
there.

| Symptom | Likely cause | Fix |
|---|---|---|
| HTTP **500.19** | Hosting Bundle not installed, or installed before IIS | Reinstall it (1.3), restart IIS |
| HTTP **500.30** / **500.31** | App crashed at startup / .NET 8 runtime missing | Event Viewer; see "stdout logs" below |
| Event log: `Login failed for user 'IIS APPPOOL\JobMatch'` | DB login missing | Re-run 1.7 |
| `RedisConnectionException` in logs | Wrong password / Redis service stopped | `Get-Service Redis`; re-run 1.8 |
| Meta "callback URL couldn't be validated" (`GET /webhook` → 403) | Verify token mismatch, or HTTPS not bound yet | Re-run 1.8; check 1.9 |
| Webhook POSTs get **403** | Wrong App Secret (signature check fails) | Re-run 1.8 with the App Secret from App settings → Basic |
| Messages arrive but no replies | Access token blank (dry-run) or expired | Re-run 1.8 with a permanent token |
| Replies stop after a quiet spell | App pool idled out | Do 1.10 |

**stdout logs** (temporary, for startup crashes): in the live release's `app\web.config` set
`stdoutLogEnabled="true"` and `stdoutLogFile="C:\inetpub\jobmatch\logs\stdout"` (the pool can only
write to `logs\`). Turn it off again afterwards, because the files grow unbounded. The next deploy
resets it anyway.

**Is data being captured?** The research dataset is the `MessageLog` table:
```sql
SELECT TOP 20 [Timestamp], WaId, Direction, SessionState, IntentDetected, NluConfidence, IsRepair
FROM JobMatchGhana.dbo.MessageLogs ORDER BY Id DESC;
```

---

## 5. Later slices on this server

- **Slice 2 (Python NLP service):** run uvicorn as a Windows service (NSSM) bound to
  `127.0.0.1:8000`, then set `Nlu__UseStub=false` on the app pool. A deploy script for it will
  come with Slice 2.
- **Slice 4 (admin SPA):** static files under the same site or a sibling site.
- **Azure move (Slice 5):** App Service on Windows is IIS underneath, so the app, `web.config` and
  migration script carry over. App-pool variables become App Service *Application settings*, and
  `AlwaysRunning` becomes the *Always On* switch.
